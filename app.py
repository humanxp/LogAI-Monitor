# Copyright (C) 2026 Fotios Tsiadimos
# SPDX-License-Identifier: GPL-3.0-or-later

import os
import time
import threading
from queue import Queue, Full
from datetime import datetime, timezone
from functools import wraps
from flask import Flask, render_template, request, jsonify, redirect, url_for, flash
from flask_socketio import SocketIO, emit
from flask_login import LoginManager, UserMixin, login_user, logout_user, login_required, current_user
from werkzeug.security import generate_password_hash, check_password_hash
from apscheduler.schedulers.background import BackgroundScheduler
from apscheduler.events import EVENT_JOB_ERROR, EVENT_JOB_MAX_INSTANCES, EVENT_JOB_MISSED
import redis.exceptions
from config import Config
from services.redis_client import redis_client
from services.syslog_receiver import SyslogReceiver
from services.docker_collector import DockerLogCollector
from services.ollama_analyzer import OllamaAnalyzer
from services.telegram_notifier import telegram_notifier

# Initialize Flask app
app = Flask(__name__)
app.config.from_object(Config)
app.config['SECRET_KEY'] = os.environ.get('SECRET_KEY', 'logai-monitor-secret-key-change-in-production')

# make the current version available in templates (globals are easier than
# passing to every render call)
app.jinja_env.globals['VERSION'] = Config.VERSION

# Track Redis connection status
_redis_available = False
_redis_error_message = None

# --- Batch-analysis trigger state -----------------------------------------
# The pending backlog is tracked here: incremented on every stored log and
# decremented when an analysis run fetches them. When it reaches the configured
# batch size, analysis is triggered immediately, so batches are sent to the AI
# EITHER every ANALYSIS_INTERVAL_MINUTES OR as soon as MAX_LOGS_PER_ANALYSIS
# unanalyzed logs have accumulated -- whichever comes first.
_pending_logs = 0
_pending_lock = threading.Lock()
# Single-flight guard shared by the scheduler job and threshold triggers so
# only one analysis runs at a time.
_analysis_lock = threading.Lock()
# Monotonic throttle between threshold-triggered attempts (prevents hammering
# the AI endpoint while it is down: one retry per interval at most).
_last_batch_trigger_ts = [0.0]
_BATCH_TRIGGER_MIN_INTERVAL = float(os.environ.get('BATCH_TRIGGER_MIN_INTERVAL', 30))

# --- Per-log worker pool --------------------------------------------------
# Storing a log + emitting it is cheap, but the follow-up work (filter
# matching, alert creation, AI-generated Telegram alerts) hits Redis, the AI
# endpoint and Telegram - all potentially slow.  Doing that synchronously on
# the syslog UDP/TCP receive thread makes the receiver block and drop packets
# during bursts.  The receiver threads therefore only do the fast path and the
# rest is handed to a bounded worker pool (drop-oldest when saturated).
_log_work_queue = Queue(maxsize=10000)
_LOG_WORKERS = max(1, int(os.environ.get('LOG_WORKERS', 4)))
_log_workers_started = False

def _enqueue_log_work(fn, *args):
    """Hand a slow per-log job to the worker pool. When the queue is full the
    OLDEST pending job is dropped first (newest data wins under a flood)."""
    item = (fn, args)
    try:
        _log_work_queue.put_nowait(item)
    except Full:
        try:
            _log_work_queue.get_nowait()
            _log_work_queue.task_done()
        except Exception:
            pass
        try:
            _log_work_queue.put_nowait(item)
        except Full:
            pass

def _log_worker_loop():
    while True:
        try:
            item = _log_work_queue.get()
        except Exception:
            return
        if item is None:
            return
        try:
            fn, args = item
            fn(*args)
        except Exception as e:
            print(f"[App] log worker error: {type(e).__name__}: {e}")
        finally:
            _log_work_queue.task_done()

def start_log_workers():
    """Start the bounded worker pool (idempotent)."""
    global _log_workers_started
    if _log_workers_started:
        return
    for _ in range(_LOG_WORKERS):
        t = threading.Thread(target=_log_worker_loop, daemon=True,
                             name='log-worker')
        t.start()
    _log_workers_started = True

def check_redis_connection():
    """Check if Redis is available and update status"""
    global _redis_available, _redis_error_message
    try:
        if redis_client.ping():
            _redis_available = True
            _redis_error_message = None
            return True
    except redis.exceptions.ConnectionError as e:
        _redis_available = False
        _redis_error_message = str(e)
        print(f"[App] Redis connection error: {e}")
    except Exception as e:
        _redis_available = False
        _redis_error_message = str(e)
        print(f"[App] Redis error: {e}")
    return False

def render_redis_error():
    """Render the Redis connection error page"""
    return render_template('error.html',
        icon='database',
        icon_color='#dc3545',
        title='Redis Connection Failed',
        message='LogRadarAI cannot connect to Redis. Redis is required for storing logs, settings, and user data.',
        details=[
            'Make sure Redis server is running: <code>redis-server</code> or <code>systemctl start redis</code>',
            'Check Redis is listening on the correct port: <code>redis-cli ping</code>',
            f'Current configuration: <code>{Config.REDIS_HOST}:{Config.REDIS_PORT}</code>',
            'If using Docker: <code>docker run -d -p 6379:6379 redis</code>',
            'Check firewall settings if Redis is on a remote host'
        ],
        details_title='How to fix this',
        error_code=_redis_error_message or 'Connection refused',
        show_home_link=False
    ), 503

def require_redis(f):
    """Decorator to check Redis connection before executing route"""
    @wraps(f)
    def decorated_function(*args, **kwargs):
        global _redis_available, _redis_error_message
        if not _redis_available:
            if not check_redis_connection():
                return render_redis_error()
        try:
            return f(*args, **kwargs)
        except redis.exceptions.ConnectionError as e:
            _redis_available = False
            _redis_error_message = str(e)
            return render_redis_error()
    return decorated_function

def require_redis_api(f):
    """Decorator for API routes - returns JSON error instead of HTML"""
    @wraps(f)
    def decorated_function(*args, **kwargs):
        global _redis_available, _redis_error_message
        if not _redis_available:
            if not check_redis_connection():
                return jsonify({
                    'error': 'Redis connection failed',
                    'message': _redis_error_message or 'Connection refused',
                    'redis_available': False
                }), 503
        try:
            return f(*args, **kwargs)
        except redis.exceptions.ConnectionError as e:
            _redis_available = False
            _redis_error_message = str(e)
            return jsonify({
                'error': 'Redis connection failed',
                'message': str(e),
                'redis_available': False
            }), 503
    return decorated_function

# Initialize Flask-Login
login_manager = LoginManager()
login_manager.init_app(app)
login_manager.login_view = 'login'
login_manager.login_message = 'Please log in to access this page.'
login_manager.login_message_category = 'info'

# User class for Flask-Login
class User(UserMixin):
    def __init__(self, user_data):
        self.id = user_data.get('id')
        self.username = user_data.get('username')
        self.email = user_data.get('email', '')
        self.role = user_data.get('role', 'user')
        self.is_admin = user_data.get('role') == 'admin'

@login_manager.user_loader
def load_user(user_id):
    try:
        user_data = redis_client.get_user(user_id)
        if user_data:
            return User(user_data)
    except redis.exceptions.ConnectionError:
        pass  # Will be handled by route decorators
    except Exception as e:
        print(f"[App] Error loading user: {e}")
    return None

# Initialize SocketIO with threading mode (more compatible)
socketio = SocketIO(app, cors_allowed_origins="*", async_mode='threading')

# Initialize services
syslog_receiver = None
docker_collector = None
ollama_analyzer = None
scheduler = None

def log_callback(log_entry):
    """Callback for new log entries.

    Fast path only (store + backlog bookkeeping + realtime emit) so the syslog
    UDP/TCP receive threads and the Docker watchers never block on Redis round
    trips or slow filter/AI/Telegram work.  The filter/alert/Telegram stage is
    handed to the bounded worker pool.
    """
    global _redis_available, _pending_logs
    try:
        # Store in Redis (single pipelined round trip)
        log_id = redis_client.store_log(log_entry)
        log_entry['id'] = log_id

        # Count toward the pending backlog and, once the batch threshold is
        # reached, kick off analysis immediately (2-min timer OR batch size).
        with _pending_lock:
            _pending_logs += 1
        maybe_trigger_batch_analysis()

        # Emit to connected clients
        socketio.emit('new_log', log_entry)
        _push_stats_if_needed()
    except redis.exceptions.ConnectionError as e:
        _redis_available = False
        print(f"[App] Redis connection lost in log_callback: {e}")
        # Still emit to clients even if Redis is down
        socketio.emit('new_log', log_entry)
        return

    # Heavy stage runs off the receive thread
    _enqueue_log_work(_process_log_filters, log_entry, log_id)


def _process_log_filters(log_entry, log_id):
    """Filter matching / alert creation / Telegram for one log.

    Runs on the log-worker pool.  Settings and filters are served from the
    short-TTL cache in RedisClient, so a steady stream of messages costs
    almost nothing when no rules are configured.
    """
    global _redis_available
    try:
        try:
            settings = redis_client.get_settings()
        except redis.exceptions.ConnectionError:
            return  # Redis is down; alerts cannot be persisted anyway
        if not settings.get('auto_analyze', True):
            return

        filters = redis_client.get_enabled_filters()
        if not filters:
            return

        matched = ollama_analyzer.check_log_against_filters(log_entry, filters)

        if matched:
            for f in matched:
                # Create alert
                alert_data = {
                    'log_id': log_id,
                    'filter_id': f.get('id'),
                    'filter_name': f.get('name'),
                    'severity': log_entry.get('severity'),
                    'source': log_entry.get('source'),
                    'hostname': log_entry.get('hostname', log_entry.get('source')),
                    'message': log_entry.get('message', '')[:500]
                }
                redis_client.store_alert(alert_data)

                # Emit alert
                socketio.emit('new_alert', alert_data)
                _push_stats_if_needed()

                # Send Telegram notification only when the log severity is
                # covered by the configured alert levels (alert_on_critical /
                # alert_on_error). Lower-severity filter hits still create UI
                # alerts but do NOT spam Telegram.
                if f.get('notify_telegram', False) and settings.get('telegram_enabled', False):
                    severity = str(log_entry.get('severity', 'info')).lower()
                    notify_levels = []
                    if settings.get('alert_on_critical', True):
                        notify_levels += ['critical', 'emergency', 'alert']
                    if settings.get('alert_on_error', True):
                        notify_levels.append('error')
                    if severity not in notify_levels:
                        continue
                    hostname = log_entry.get('hostname', log_entry.get('source', 'unknown'))
                    filter_id = f.get('id', 'unknown')
                    cooldown_minutes = int(settings.get('telegram_cooldown_minutes', 60))
                    in_cooldown = cooldown_minutes > 0 and redis_client.check_notification_cooldown(hostname, filter_id)
                    if not in_cooldown:
                        if ollama_analyzer.is_available():
                            alert_msg = ollama_analyzer.generate_alert_message(log_entry, {})
                            # Fallback to original log message if AI returns an empty/whitespace-only string
                            if not alert_msg or not alert_msg.strip():
                                alert_msg = log_entry.get('message', '')[:200]
                        else:
                            alert_msg = log_entry.get('message', '')[:200]

                        telegram_notifier.send_alert(
                            log_entry.get('severity', 'info'),
                            log_entry.get('source', 'unknown'),
                            alert_msg,
                            hostname=hostname
                        )
                        redis_client.set_notification_cooldown(hostname, filter_id, cooldown_minutes)
    except redis.exceptions.ConnectionError:
        _redis_available = False
        print("[App] Redis connection lost during filter processing")
    except Exception as e:
        print(f"[App] _process_log_filters error: {type(e).__name__}: {e}")

def _is_failed_analysis(analysis):
    """True when the AI analysis produced no usable result (parse failure / no
    response). Only these get stored in history so they can be re-analysed."""
    if not analysis:
        return True
    hay = ' '.join(
        [str(analysis.get('summary') or '')] +
        [str(i) for i in (analysis.get('issues_found') or [])] +
        [str(r) for r in (analysis.get('recommendations') or [])]
    )
    return 'Unable to parse' in hay or 'did not respond' in hay


def _make_failed_analysis(reason):
    """Marker analysis used when the AI backend did not respond or errored."""
    reason = str(reason or 'AI backend not available')[:300]
    return {
        'overall_status': 'unknown',
        'issues_found': [f'AI did not respond: {reason}'],
        'critical_count': 0,
        'recommendations': [],
        'affected_hosts': [],
        'alert_message': ''
    }


def _store_auto_history(logs, analysis):
    """Record an automatic batch analysis. When the newest auto record is a
    failed one covering the same logs (a retry of a failed batch), the record is
    updated in place instead of stacking a duplicate entry."""
    log_ids = [l.get('id') for l in logs if l.get('id')]
    newest = redis_client.get_latest_auto_history()
    if newest and _is_failed_analysis(newest.get('analysis')):
        if set(newest.get('log_ids') or []) & set(log_ids):
            redis_client.update_analysis_history(newest['id'], {
                'type': 'auto',
                'logs_analyzed': len(logs),
                'log_ids': log_ids,
                'analysis': analysis
            })
            return
    redis_client.store_analysis_history({
        'type': 'auto',
        'logs_analyzed': len(logs),
        'log_ids': log_ids,
        'analysis': analysis
    })


def _record_auto_failure(logs, reason):
    """Record a failed automatic batch analysis (AI did not respond / errored).

    The logs are deliberately NOT marked analyzed so the next run retries them
    automatically; the failed record also gives the user a manual re-analyze
    entry point. Repeated failures of the same batch only refresh the existing
    record instead of spamming the history.
    """
    analysis = _make_failed_analysis(reason)
    log_ids = [l.get('id') for l in logs if l.get('id')]
    newest = redis_client.get_latest_auto_history()
    if newest and _is_failed_analysis(newest.get('analysis')):
        old_ids = set(newest.get('log_ids') or [])
        # Same (or overlapping) batch already recorded as failed.
        if not log_ids or (old_ids & set(log_ids)):
            redis_client.update_analysis_history(newest['id'], {
                'logs_analyzed': len(logs) or newest.get('logs_analyzed') or 0,
                'log_ids': log_ids or newest.get('log_ids') or [],
                'analysis': analysis
            })
            return
    redis_client.store_analysis_history({
        'type': 'auto',
        'logs_analyzed': len(logs),
        'log_ids': log_ids,
        'analysis': analysis
    })


def _run_analysis_once():
    """Single-flight wrapper around periodic_analysis shared by the scheduler
    and the batch-threshold trigger, so both can never run concurrently."""
    if not _analysis_lock.acquire(blocking=False):
        return False
    try:
        periodic_analysis()
        return True
    finally:
        _analysis_lock.release()


def maybe_trigger_batch_analysis():
    """Called after each new log arrives: when the pending backlog reaches the
    configured batch size (max_logs_per_analysis setting), start an analysis
    run in a background thread immediately -- even if the interval timer has
    not fired yet (batch threshold OR time interval, whichever comes first).
    Throttled so a down AI backend is not hammered with retries."""
    with _pending_lock:
        pending = _pending_logs
    threshold = _max_logs_per_analysis()
    if pending < threshold:
        return
    now = time.monotonic()
    if now - _last_batch_trigger_ts[0] < _BATCH_TRIGGER_MIN_INTERVAL:
        return
    _last_batch_trigger_ts[0] = now
    threading.Thread(target=_run_analysis_once, daemon=True,
                     name='batch-trigger').start()


# Throttle the settings re-sync: /api/stats is polled ~1x/s by dashboards but
# the sync itself only needs to happen every couple of seconds.
_ai_sync_last_ts = [0.0]

# --- Runtime-tunable analysis knobs ----------------------------------------
# The .env values only seed the defaults; once saved through
# Settings -> General Settings they are read from Redis on every use, so a
# change takes effect immediately (no container rebuild / restart).

def _setting_int(key: str, fallback: int) -> int:
    """Read an integer setting from Redis with a safe fallback."""
    try:
        settings = redis_client.get_settings()
        raw = settings.get(key)
        if raw is None or raw == '':
            return int(fallback)
        return int(raw)
    except Exception:
        return int(fallback)

def _analysis_interval_minutes() -> int:
    """Current auto-analysis interval (minutes)."""
    return max(1, _setting_int('analysis_interval', Config.ANALYSIS_INTERVAL_MINUTES))

def _max_logs_per_analysis() -> int:
    """Current batch size (logs fetched per analysis run / trigger threshold)."""
    return max(1, _setting_int('max_logs_per_analysis', Config.MAX_LOGS_PER_ANALYSIS))

def _batch_sample_limit() -> int:
    """Current per-batch sampling cap sent to the AI model."""
    try:
        import os as _os
        default = int(_os.environ.get('BATCH_SAMPLE_LIMIT', 200))
    except Exception:
        default = 200
    return max(1, _setting_int('batch_sample_limit', default))

def _reschedule_analysis_job():
    """Point the periodic-analysis scheduler job at the current interval."""
    try:
        if scheduler:
            scheduler.reschedule_job(
                'periodic_analysis',
                trigger='interval',
                minutes=_analysis_interval_minutes()
            )
            print(f"[App] Periodic analysis rescheduled to every "
                  f"{_analysis_interval_minutes()} minutes")
    except Exception as e:
        print(f"[App] Failed to reschedule analysis job: {e}")


def _apply_ai_from_settings(settings=None):
    """Push provider / endpoint / model from a settings dict into the live
    analyzer.  Provider ('openai' | 'ollama') is user-selectable because the
    two protocols use different endpoints and model-list formats."""
    if not ollama_analyzer:
        return
    try:
        if not isinstance(settings, dict):
            settings = redis_client.get_settings()
        prov = str(settings.get('ai_provider') or Config.AI_PROVIDER).strip().lower()
        if prov not in ('openai', 'ollama'):
            prov = 'openai' if Config.AI_BASE_URL else 'ollama'
        model = settings.get('ollama_model') or Config.OLLAMA_MODEL
        # Endpoint fallback is provider-aware: an OpenAI-compatible backend must
        # NOT fall back to the native-Ollama OLLAMA_HOST.
        if prov == 'openai':
            endpoint = (settings.get('ollama_host') or '').strip() or Config.AI_BASE_URL
        else:
            endpoint = (settings.get('ollama_host') or '').strip() or Config.OLLAMA_HOST
        if (ollama_analyzer.provider != prov
                or ollama_analyzer.host != (endpoint or '').rstrip('/')
                or ollama_analyzer.model != model):
            ollama_analyzer.apply_endpoint(host=endpoint or None, model=model,
                                           provider=prov)
    except Exception as e:
        print(f"[App] _apply_ai_from_settings failed: {e}")


def _sync_analyzer_settings():
    """Keep the analyzer pointed at the provider/model/host configured in
    Settings (falling back to env defaults), so a switch applies immediately
    instead of only after the settings-page status check or a restart."""
    if not ollama_analyzer:
        return
    now = time.time()
    if now - _ai_sync_last_ts[0] < 2:
        return
    try:
        settings = redis_client.get_settings()
        _apply_ai_from_settings(settings)
        # The per-batch sampling cap is tunable in Settings as well
        sample = _batch_sample_limit()
        if getattr(ollama_analyzer, 'batch_sample_limit', None) != sample:
            ollama_analyzer.batch_sample_limit = sample
        _ai_sync_last_ts[0] = now
    except Exception as e:
        print(f"[App] _sync_analyzer_settings failed: {e}")


_MAX_FAILED_BATCH_RETRIES = 3


def _dead_letter_stuck_batch(logs):
    """Retire a batch that keeps failing to parse.

    Called right after _record_auto_failure() (which guarantees the newest
    failed history record covers THIS batch). Counts consecutive failures on
    that record; once the batch has failed _MAX_FAILED_BATCH_RETRIES times it
    is marked analyzed so it leaves logs:unanalyzed and the queue can move on.
    """
    newest = redis_client.get_latest_auto_history()
    if not newest or not _is_failed_analysis(newest.get('analysis')):
        return
    tries = int(newest.get('fail_count') or 0) + 1
    redis_client.update_analysis_history(newest['id'], {'fail_count': str(tries)})
    if tries < _MAX_FAILED_BATCH_RETRIES:
        return
    reason = newest.get('analysis') or 'analysis call failed'
    redis_client.mark_logs_analyzed(logs, json.dumps(reason, ensure_ascii=False))
    print(f"[Scheduler] Dead-lettered {len(logs)} logs after {tries} consecutive "
          "failures; moving on to newer batches")


def periodic_analysis():
    """Periodic log analysis task (also invoked by the batch-size trigger)."""
    global _pending_logs
    # Scheduler liveness heartbeat for the health watchdog (set even when the
    # backlog is empty - a running-but-idle scheduler is healthy).
    _last_analysis_run_ts[0] = time.time()
    settings = redis_client.get_settings()

    if not settings.get('auto_analyze', True):
        return

    if not ollama_analyzer:
        return

    # Use the currently configured model/host from Settings.
    _sync_analyzer_settings()

    # Get unanalyzed logs (oldest first, so nothing is ever starved)
    logs = redis_client.get_unanalyzed_logs(limit=_max_logs_per_analysis())

    if not logs:
        return

    # These logs are now "in flight": take them out of the pending backlog so a
    # fresh batch can trigger while this one is still being analyzed.
    with _pending_lock:
        _pending_logs = max(0, _pending_logs - len(logs))

    # No availability probe here: a /models check can false-negative while the
    # AI server is briefly busy/down, skipping the batch. Just attempt the real
    # analysis - if the backend is unreachable it fails fast (8s connect
    # timeout) and the failure is recorded below with the logs left unanalyzed
    # for the automatic retry.

    print(f"[Scheduler] Analyzing {len(logs)} logs...")

    # Batch analysis
    result = ollama_analyzer.analyze_logs_batch(logs)

    if result.get('success'):
        analysis = result.get('analysis', {})

        # Store in history (updates a previous failed record for the same batch)
        _store_auto_history(logs, analysis)

        # Drop stale FAILED records whose logs just got analyzed successfully
        # (older retry-round leftovers that confuse the AI History page).
        try:
            redis_client.purge_stale_failed_history(limit=30)
        except Exception as _e:
            print(f"[App] purge_stale_failed_history error: {_e}")

        # Mark logs as analyzed (single pipelined round trip instead of N*2)
        redis_client.mark_logs_analyzed(logs, str(analysis))

        # Send Telegram summary if critical issues found
        if analysis.get('overall_status') == 'critical' and settings.get('telegram_enabled'):
            stats = redis_client.get_stats()
            telegram_notifier.send_summary(stats, analysis)

        # Emit analysis result
        socketio.emit('analysis_complete', {
            'logs_analyzed': len(logs),
            'analysis': analysis
        })
        _push_stats_if_needed()
    else:
        # The AI backend answered the availability probe but the analysis call
        # itself failed (timeout / HTTP error / unusable response). Record the
        # failure; the logs are intentionally left unanalyzed so the next run
        # retries them automatically.
        reason = result.get('error') or 'analysis call failed'
        print(f"[Scheduler] Analysis failed: {reason}")
        _record_auto_failure(logs, reason)
        # Dead-letter guard: a batch that the model cannot parse must not wedge
        # the queue forever (failed logs are deliberately retried). After
        # _MAX_FAILED_BATCH_RETRIES consecutive failures the batch is retired
        # ("dead-lettered") so the scheduler moves on to newer logs.
        try:
            _dead_letter_stuck_batch(logs)
        except Exception as _e:
            print(f"[Scheduler] dead-letter guard error: {_e}")

def cleanup_task():
    """Periodic cleanup of old logs (improved diagnostics). Uses the configured
    'log_retention_hours' setting when available, otherwise falls back to Config."""
    ts = datetime.now(timezone.utc).isoformat()
    try:
        # Determine retention to use for this run (prefer saved settings)
        try:
            settings = redis_client.get_settings()
            retention = int(settings.get('log_retention_hours', Config.LOG_RETENTION_HOURS))
        except Exception:
            retention = Config.LOG_RETENTION_HOURS

        try:
            app.logger.info(f"[Scheduler] Running cleanup now (will use retention={retention}h)")
        except Exception:
            print(f"[Scheduler] Running cleanup now (will use retention={retention}h)")

        count = redis_client.cleanup_old_logs(retention_hours=retention)
        try:
            redis_client.purge_stale_failed_history()
        except Exception as _e:
            print(f"[Scheduler] purge_stale_failed_history error: {_e}")
        status = redis_client.get_cleanup_status()
        msg = (f"[Scheduler] Cleanup run at {ts} - removed {count} old logs "
               f"(used_retention={retention}h) | timeline_count={status.get('timeline_count')} "
               f"last_run={status.get('last_run')} last_removed={status.get('last_removed')}")
        try:
            app.logger.info(msg)
        except Exception:
            print(msg)
    except redis.exceptions.ConnectionError as e:
        print(f"[Scheduler] Redis connection error during cleanup: {e}")
    except Exception as e:
        print(f"[Scheduler] Cleanup error: {e}")

# Telegram alerts for scheduler events (errors only; missed/skipped runs are
# normal under load and would only spam Telegram)
_LAST_SCHEDULER_ALERT_TS = [0.0]

def _scheduler_event_listener(event):
    """Send a Telegram alert ONLY when a scheduled job actually errors."""
    try:
        if event.code != EVENT_JOB_ERROR:
            return
        now = time.time()
        # Throttle: at most one alert per 15 minutes to avoid spam
        if now - _LAST_SCHEDULER_ALERT_TS[0] < 15 * 60:
            return
        reason = f"执行出错：{getattr(event, 'exception', 'unknown')}"
        ok = telegram_notifier.send_message(
            f'⚠️ <b>LogRadarAI 调度器提醒</b>\n'
            f'任务 <code>{event.job_id}</code> {reason}'
        )
        if ok:
            _LAST_SCHEDULER_ALERT_TS[0] = now
    except Exception as e:
        print(f'[Scheduler] Telegram alert failed: {e}')

def init_services():
    """Initialize all services"""
    global syslog_receiver, docker_collector, ollama_analyzer, scheduler, _redis_available
    
    # Check Redis connection first
    print("[App] Checking Redis connection...")
    if not check_redis_connection():
        print(f"[App] WARNING: Redis is not available! Error: {_redis_error_message}")
        print("[App] Some features will be unavailable until Redis is connected.")
        print("[App] Starting services anyway - Redis can be connected later.")
    else:
        print("[App] Redis connection: OK")
    
    # Initialize Ollama analyzer
    ollama_analyzer = OllamaAnalyzer(cache_ttl=Config.OLLAMA_CACHE_TTL_SECONDS)
    # Prime Ollama availability asynchronously so dashboard shows correct status quickly
    try:
        import threading as _th
        def _prime_ollama():
            try:
                print('[App] Priming Ollama availability check')
                ollama_analyzer.is_available(force_refresh=True)
                print('[App] Ollama availability primed')
            except Exception as _e:
                print(f'[App] Ollama prime failed: {_e}')
        _th.Thread(target=_prime_ollama, daemon=True).start()
    except Exception as _e:
        print(f"[App] Failed to start Ollama prime thread: {_e}")

    # Initialize syslog receiver
    syslog_receiver = SyslogReceiver(callback=log_callback)
    # Bounded worker pool for the per-log filter/alert/Telegram stage, so the
    # UDP/TCP receive threads only enqueue and never block on slow work.
    start_log_workers()
    syslog_receiver.start()
    
    # Initialize Docker collector with settings provider
    def safe_get_settings():
        """Wrapper to safely get settings even if Redis is down"""
        try:
            return redis_client.get_settings()
        except redis.exceptions.ConnectionError:
            return redis_client.get_default_settings()
    
    docker_collector = DockerLogCollector(
        callback=log_callback,
        settings_provider=safe_get_settings
    )
    docker_collector.start()
    
    # Initialize Telegram from settings (only if Redis is available)
    if _redis_available:
        try:
            settings = redis_client.get_settings()
            if settings.get('telegram_bot_token') and settings.get('telegram_chat_id'):
                telegram_notifier.configure(
                    settings['telegram_bot_token'],
                    settings['telegram_chat_id']
                )
        except redis.exceptions.ConnectionError:
            print("[App] Could not load Telegram settings - Redis unavailable")
    
    # Initialize scheduler
    scheduler = BackgroundScheduler()
    
    # Get analysis interval (use default if Redis unavailable); the interval
    # is re-read from Settings on every save (rescheduled live, no restart).
    analysis_interval = _analysis_interval_minutes()
    
    scheduler.add_job(
        _run_analysis_once,
        'interval',
        minutes=analysis_interval,
        id='periodic_analysis'
    )
    # Schedule cleanup to run immediately and then every hour
    scheduler.add_job(cleanup_task, 'interval', hours=1, next_run_time=datetime.now(timezone.utc), id='cleanup_task')
    try:
        app.logger.info("[Scheduler] Jobs registered; cleanup scheduled to run immediately")
    except Exception:
        print("[Scheduler] Jobs registered; cleanup scheduled to run immediately")
    
    # Add periodic Redis health check
    def check_redis_health():
        global _redis_available
        was_available = _redis_available
        check_redis_connection()
        if not was_available and _redis_available:
            print("[App] Redis connection restored!")
            # Try to ensure admin exists now that Redis is back
            try:
                redis_client.ensure_admin_exists()
            except:
                pass
        elif was_available and not _redis_available:
            print("[App] Redis connection lost!")
    
    scheduler.add_job(check_redis_health, 'interval', seconds=30, id='check_redis_health')

    # Health watchdog: self-check + Telegram alerts + daily summary
    scheduler.add_job(health_watchdog, 'interval', minutes=_HEALTH_WATCH_MINUTES,
                      next_run_time=datetime.now(timezone.utc), id='health_watchdog')

    # Alert via Telegram when scheduled jobs are missed / skipped / error
    scheduler.add_listener(
        _scheduler_event_listener,
        EVENT_JOB_MISSED | EVENT_JOB_MAX_INSTANCES | EVENT_JOB_ERROR
    )
    print('[Scheduler] Telegram alerts enabled for missed/skipped/error events')
    scheduler.start()
    # Health-watchdog heartbeat baseline: the first periodic run may take up to
    # one interval, so never alert "scheduler stuck" right after boot.
    _last_analysis_run_ts[0] = time.time()
    try:
        app.logger.info("[Scheduler] started")
    except Exception:
        print("[Scheduler] started")

    # Seed the pending backlog counter so a backlog left over from a restart is
    # analyzed promptly (the batch-threshold trigger becomes active again).
    global _pending_logs
    try:
        threshold = _max_logs_per_analysis()
        with _pending_lock:
            _pending_logs = len(redis_client.get_unanalyzed_logs(
                limit=threshold + 1))
        if _pending_logs >= threshold:
            maybe_trigger_batch_analysis()
    except Exception as e:
        print(f"[App] Failed to initialize pending-log counter: {e}")
        _pending_logs = 0
    
    # Ensure at least one admin user exists (only if Redis is available)
    if _redis_available:
        try:
            redis_client.ensure_admin_exists()
        except redis.exceptions.ConnectionError:
            print("[App] Could not ensure admin exists - Redis unavailable")
    
    print("[App] All services initialized")

# ==================== AUTH ROUTES ====================

@app.route('/login', methods=['GET', 'POST'])
def login():
    """Login page"""
    # Check Redis before allowing login
    if not _redis_available:
        if not check_redis_connection():
            return render_redis_error()
    
    if current_user.is_authenticated:
        return redirect(url_for('index'))
    
    if request.method == 'POST':
        username = request.form.get('username', '').strip()
        password = request.form.get('password', '')
        remember = request.form.get('remember', False)
        
        try:
            user_data = redis_client.get_user_by_username(username)
            
            if user_data and check_password_hash(user_data.get('password_hash', ''), password):
                user = User(user_data)
                login_user(user, remember=remember)
                
                next_page = request.args.get('next')
                if next_page:
                    return redirect(next_page)
                return redirect(url_for('index'))
            
            flash('Invalid username or password', 'error')
        except redis.exceptions.ConnectionError:
            return render_redis_error()
    
    return render_template('login.html')

@app.route('/logout')
@login_required
def logout():
    """Logout"""
    logout_user()
    flash('You have been logged out.', 'info')
    return redirect(url_for('login'))

# ==================== WEB ROUTES ====================

@app.route('/')
@login_required
@require_redis
def index():
    """Main dashboard"""
    return render_template('index.html')

@app.route('/logs')
@login_required
@require_redis
def logs():
    """Logs page"""
    return render_template('logs.html')

@app.route('/clients')
@login_required
@require_redis
def clients():
    """Connected syslog clients page"""
    return render_template('clients.html')

@app.route('/filters')
@login_required
@require_redis
def filters():
    """Filters page"""
    return render_template('filters.html')

@app.route('/alerts')
@login_required
@require_redis
def alerts():
    """Alerts page"""
    return render_template('alerts.html')

@app.route('/docker')
@login_required
@require_redis
def docker():
    """Docker containers page"""
    return render_template('docker.html')

@app.route('/analysis')
@login_required
@require_redis
def analysis():
    """AI Analysis page"""
    return render_template('analysis.html')

@app.route('/ai-history')
@login_required
@require_redis
def ai_history():
    """AI Analysis History page"""
    return render_template('ai_history.html')

@app.route('/settings')
@login_required
@require_redis
def settings():
    """Settings page"""
    return render_template('settings.html')

@app.route('/users')
@login_required
@require_redis
def users():
    """User management page (admin only)"""
    if not current_user.is_admin:
        flash('Access denied. Admin privileges required.', 'error')
        return redirect(url_for('index'))
    return render_template('users.html')

@app.route('/about')
@login_required
@require_redis
def about():
    """About page"""
    # pass version explicitly in case someone overrides the global later
    return render_template('about.html', version=Config.VERSION)

# ==================== API ROUTES ====================

# --- Stats: shared payload + throttled realtime push ---------------------
# Open dashboards used to poll /api/stats about once per second per tab while
# the log stream was active (each new_log event triggered a throttled fetch).
# The server now pushes a 'stats' socket event whenever data actually changes,
# throttled to ~2s, so clients update without hammering Redis/HTTP.

def _collect_stats_payload():
    """One dict used by both /api/stats and the realtime 'stats' broadcast."""
    stats = redis_client.get_stats()
    stats['redis_connected'] = redis_client.ping()
    # Keep the analyzer pointed at the model configured in Settings so the
    # dashboard label matches even right after a restart (the instance starts
    # from the env default until the first analysis syncs it).
    _sync_analyzer_settings()
    # Use cached availability to avoid triggering a list() call on every push
    if ollama_analyzer:
        stats['ollama_available'] = ollama_analyzer.cached_availability()
        stats['ollama_last_check_age'] = ollama_analyzer.last_check_age()
        stats['ai_model'] = ollama_analyzer.model
        # If we have never checked or cache is stale, trigger a background refresh so UI updates quickly
        try:
            last_age = stats['ollama_last_check_age']
            # If never checked or older than TTL and no check in progress, start one
            if (last_age is None or (last_age is not None and last_age > ollama_analyzer._cache_ttl)) and not getattr(ollama_analyzer, '_check_lock').locked():
                import threading as _th
                def _refresh():
                    try:
                        print('[App] Background refresh of Ollama availability')
                        ollama_analyzer.is_available(force_refresh=True)
                        print('[App] Background refresh complete')
                    except Exception as _e:
                        print(f'[App] Background refresh failed: {_e}')
                _th.Thread(target=_refresh, daemon=True).start()
        except Exception as _e:
            print(f"[App] Error scheduling Ollama background refresh: {_e}")
    else:
        stats['ollama_available'] = False
        stats['ollama_last_check_age'] = None
        stats['ai_model'] = ''
    stats['telegram_enabled'] = telegram_notifier.enabled
    return stats

_stats_push_lock = threading.Lock()
_stats_push_last = [0.0]
_STATS_PUSH_INTERVAL = 2.0  # matches redis_client.STATS_CACHE_TTL

def _push_stats_if_needed():
    """Broadcast fresh stats to all dashboards, at most once per interval.
    Cheap when nothing changed (get_stats() is cached); safe to call from the
    receiver threads, scheduler and API handlers."""
    with _stats_push_lock:
        now = time.time()
        if now - _stats_push_last[0] < _STATS_PUSH_INTERVAL:
            return
        _stats_push_last[0] = now
    try:
        socketio.emit('stats', _collect_stats_payload())
    except Exception as _e:
        print(f"[App] stats push failed: {_e}")

# --- Health watchdog -------------------------------------------------------
# Periodic self-check that alerts via Telegram when something is wrong:
#   * unanalyzed backlog above HEALTH_BACKLOG_WARN
#   * AI backend unavailable (recent probe failed)
#   * periodic-analysis scheduler appears stuck (no run for ~3 intervals)
# Plus an optional daily summary.  Alerts are throttled per condition so a
# down AI server cannot spam the chat.  Knobs default from env and can be
# overridden live from Settings -> General Settings (health_* keys).
_last_analysis_run_ts = [0.0]
_health_last_alert = {}   # condition -> epoch of last alert/recovery state
_HEALTH_WATCH_MINUTES = max(1, int(os.environ.get('HEALTH_WATCH_MINUTES', 5)))  # startup default
_HEALTH_BACKLOG_WARN_DEFAULT = int(os.environ.get('HEALTH_BACKLOG_WARN', 2000))
_HEALTH_COOLDOWN_MIN_DEFAULT = int(os.environ.get('HEALTH_ALERT_COOLDOWN_MIN', 30))
_HEALTH_DAILY_SUMMARY_DEFAULT = os.environ.get('HEALTH_DAILY_SUMMARY', '1').lower() in ('1', 'true', 'yes')

def _health_warn_threshold() -> int:
    return max(0, _setting_int('health_backlog_warn', _HEALTH_BACKLOG_WARN_DEFAULT))

def _health_cooldown_s() -> int:
    return max(60, _setting_int('health_alert_cooldown_min', _HEALTH_COOLDOWN_MIN_DEFAULT) * 60)

def _health_daily_summary() -> bool:
    try:
        v = redis_client.get_settings().get('health_daily_summary', _HEALTH_DAILY_SUMMARY_DEFAULT)
        return str(v).lower() in ('1', 'true', 'yes')
    except Exception:
        return _HEALTH_DAILY_SUMMARY_DEFAULT

def _reschedule_health_job():
    """Point the health-watchdog job at the current interval from Settings."""
    try:
        if scheduler:
            minutes = _setting_int('health_watch_minutes', _HEALTH_WATCH_MINUTES)
            scheduler.reschedule_job('health_watchdog', trigger='interval',
                                     minutes=max(1, minutes))
            print(f"[App] Health watchdog rescheduled to every {max(1, minutes)} minutes")
    except Exception as e:
        print(f"[App] Failed to reschedule health job: {e}")

def _health_send(text):
    """Send a health message via Telegram (prints when disabled/failed)."""
    if not telegram_notifier.enabled:
        print(f"[Health] (telegram disabled) {text[:160]}")
        return False
    try:
        return telegram_notifier.send_message(text)
    except Exception as e:
        print(f"[Health] telegram error: {e}")
        return False

def health_watchdog():
    """Scheduled self-check: alert anomalies, send daily summary."""
    try:
        now = time.time()
        conds = []   # (condition_key, message)

        try:
            unanalyzed = int(redis_client.client.zcard('logs:unanalyzed'))
        except Exception:
            unanalyzed = -1
        if unanalyzed > _health_warn_threshold():
            conds.append(('backlog',
                          f'⚠️ 未分析日志积压 <b>{unanalyzed}</b> 条（阈值 {_health_warn_threshold()}），AI 分析跟不上'))

        ai_state = 'unchecked'
        try:
            if ollama_analyzer:
                age = ollama_analyzer.last_check_age()
                if ollama_analyzer.cached_availability():
                    ai_state = 'ok'
                elif age is not None and age < ollama_analyzer._cache_ttl * 3:
                    ai_state = 'down'
                else:
                    # probe stale: refresh in background (its lock dedups)
                    import threading as _th
                    _th.Thread(target=lambda: ollama_analyzer.is_available(force_refresh=True),
                               daemon=True, name='health-ai-probe').start()
        except Exception as e:
            print(f"[Health] ai probe error: {e}")
        if ai_state == 'down':
            conds.append(('ai', '⚠️ <b>AI 后端不可达</b>（模型 '
                          f'{getattr(ollama_analyzer, "model", "?")} @ '
                          f'{getattr(ollama_analyzer, "host", "?")}）'))

        interval_s = _analysis_interval_minutes() * 60
        age = (now - _last_analysis_run_ts[0]) if _last_analysis_run_ts[0] else -1
        if age >= 0 and age > max(interval_s * 2.5, 300):
            conds.append(('sched', f'⚠️ 自动分析已 {int(age // 60)} 分钟未运行（疑似调度器卡死）'))

        # Send alert for each active problem (cooldown per condition)
        for cond, msg in conds:
            if now - _health_last_alert.get(cond, 0) >= _health_cooldown_s():
                if _health_send(f'🚨 <b>LogAI Monitor 巡检告警</b>\n{msg}'):
                    _health_last_alert[cond] = now

        # Recovery notices for conditions that were alerting and now cleared
        if 'backlog' in _health_last_alert and 0 <= unanalyzed <= _health_warn_threshold():
            _health_send('✅ <b>LogAI Monitor</b>\n积压已回落到正常范围')
            _health_last_alert.pop('backlog', None)
        if 'ai' in _health_last_alert and ai_state == 'ok':
            _health_send('✅ <b>LogAI Monitor</b>\nAI 后端已恢复')
            _health_last_alert.pop('ai', None)

        # Daily summary (timestamp persisted in Redis so a container restart
        # does not re-send the summary)
        try:
            last_sum = float(redis_client.client.get('health:last_summary') or 0)
        except Exception:
            last_sum = 0
        if _health_daily_summary() and (now - last_sum >= 24 * 3600):
            try:
                mem = redis_client.client.info('memory').get('used_memory_human', '?')
            except Exception:
                mem = '?'
            total = redis_client.get_logs_count()
            ai_icon = '✅' if ai_state == 'ok' else ('⚠️' if ai_state == 'down' else '❓')
            age_txt = (f'{int(age // 60)} 分钟前' if age >= 0 else '尚未运行')
            if _health_send(
                    f'📊 <b>LogAI Monitor 巡检日报</b>\n'
                    f'日志总量：<b>{total}</b>\n'
                    f'未分析积压：{unanalyzed}\n'
                    f'AI 后端：{ai_icon} {getattr(ollama_analyzer, "model", "?")}\n'
                    f'Redis 内存：{mem}\n'
                    f'最近自动分析：{age_txt}'):
                try:
                    redis_client.client.set('health:last_summary', str(now))
                except Exception:
                    pass

        if conds:
            print(f"[Health] problems: {[c for c, _ in conds]}")
        elif ai_state == 'unchecked':
            print("[Health] OK (AI probe refreshing)")
        else:
            print("[Health] OK")
    except Exception as e:
        print(f"[Health] watchdog error: {e}")

@app.route('/api/health')
@require_redis_api
def api_health():
    """Machine-readable health summary for external polling scripts."""
    out = {'ok': True, 'ts': datetime.now(timezone.utc).isoformat()}
    try:
        out['backlog'] = int(redis_client.client.zcard('logs:unanalyzed'))
        out['total_logs'] = redis_client.get_logs_count()
        out['ai_model'] = getattr(ollama_analyzer, 'model', '')
        out['ai_available'] = bool(ollama_analyzer and ollama_analyzer.cached_availability())
        out['last_analysis_age_s'] = int(time.time() - _last_analysis_run_ts[0]) if _last_analysis_run_ts[0] else -1
        out['ok'] = (out['backlog'] <= _health_warn_threshold()
                     and (out['ai_available'] or out['last_analysis_age_s'] < 0)
                     and out['last_analysis_age_s'] <= max(_analysis_interval_minutes() * 150, 600))
    except Exception as e:
        out['ok'] = False
        out['error'] = str(e)
    return jsonify(out)

@app.route('/api/stats')
@require_redis_api
def api_stats():
    """Get system statistics"""
    return jsonify(_collect_stats_payload())

@app.route('/api/logs')
@require_redis_api
def api_logs():
    """Get logs with optional filtering"""
    limit = request.args.get('limit', 100, type=int)
    offset = request.args.get('offset', 0, type=int)
    source = request.args.get('source')
    host = request.args.get('host')
    severity = request.args.get('severity')
    search = request.args.get('search')
    
    logs = redis_client.get_logs(
        limit=limit,
        offset=offset,
        source=source,
        host=host,
        severity=severity,
        search=search
    )

    # When index filters (host/source/severity) are active, report the
    # FILTERED total so the frontend paginator walks only the matching
    # records - the raw timeline count would make the pager behave as if the
    # filter was not applied.  (Free-text search alone still uses the
    # timeline count because it is evaluated after the newest-window fetch.)
    if host or source or severity:
        total = redis_client.get_filtered_log_count(source=source, host=host,
                                                    severity=severity)
    else:
        total = redis_client.get_logs_count()

    return jsonify({
        'logs': logs,
        'count': len(logs),
        'total': total
    })

@app.route('/api/logs/<log_id>')
@require_redis_api
def api_log_detail(log_id):
    """Get a single log entry"""
    log = redis_client.get_log(log_id)
    if not log:
        return jsonify({'error': 'Log not found'}), 404
    return jsonify(log)

# Clear all logs API
@app.route('/api/logs/clear', methods=['POST'])
@require_redis_api
def api_clear_all_logs():
    """Delete all logs from the database"""
    global _pending_logs
    count = redis_client.clear_all_logs()
    # Clearing the whole database also wipes the Connected-Clients records
    redis_client.clear_syslog_clients()
    with _pending_lock:
        _pending_logs = 0
    _push_stats_if_needed()
    return jsonify({'status': 'ok', 'deleted': count})

# Delete one host: its client record AND every log it sent (admin only)
@app.route('/api/logs/delete-source', methods=['POST'])
@login_required
@require_redis_api
def api_delete_logs_by_source():
    """Permanently remove a host from the Connected Clients list together with
    every log received from its source IP."""
    if not current_user.is_admin:
        return jsonify({'error': 'Access denied'}), 403
    data = request.get_json() or {}
    source = (data.get('source') or '').strip()
    if not source:
        return jsonify({'error': 'source required'}), 400
    count = redis_client.delete_logs_by_source(source)
    client_removed = False
    if syslog_receiver:
        client_removed = syslog_receiver.delete_client(source)
    global _pending_logs
    with _pending_lock:
        _pending_logs = max(0, _pending_logs - count)
    _push_stats_if_needed()
    return jsonify({'status': 'ok', 'deleted': count, 'client_deleted': client_removed})

# Manual cleanup (admin only)
@app.route('/api/logs/cleanup', methods=['POST'])
@login_required
@require_redis_api
def api_cleanup_old_logs():
    """Trigger immediate cleanup of logs older than retention period (admin only)"""
    if not current_user.is_admin:
        return jsonify({'error': 'Access denied'}), 403
    try:
        # Use configured retention if available
        try:
            settings = redis_client.get_settings()
            retention = int(settings.get('log_retention_hours', Config.LOG_RETENTION_HOURS))
        except Exception:
            retention = Config.LOG_RETENTION_HOURS

        count = redis_client.cleanup_old_logs(retention_hours=retention)
        status = redis_client.get_cleanup_status()
        print(f"[Admin] Manual cleanup removed {count} old logs (used_retention={retention}h)")
        return jsonify({
            'status': 'ok',
            'deleted': count,
            'redis_connected': redis_client.ping(),
            'timeline_count': status.get('timeline_count'),
            'retention_hours': retention,
            'cleanup_status': status
        })
    except redis.exceptions.ConnectionError as e:
        return jsonify({'error': 'Redis connection failed', 'message': str(e)}), 503
    except Exception as e:
        return jsonify({'error': 'Cleanup failed', 'message': str(e)}), 500

# Severity tokens accepted on the HTTP ingest endpoint (anything else is
# coerced to 'info' - never stored as arbitrary attacker-controlled text).
_INGEST_SEVERITIES = {'emergency', 'alert', 'critical', 'error', 'warning',
                      'notice', 'info', 'debug'}
# Optional shared secret for the anonymous ingest endpoint. When set, the
# client must send it as the "X-Ingest-Token" header; when unset the endpoint
# stays open (legacy behaviour) but payloads are still sanitized/bounded.
_INGEST_TOKEN = os.environ.get('LOG_INGEST_TOKEN', '').strip()

def _clean_str(value, default, max_len=1000):
    """Coerce a payload field to a plain, trimmed, length-bounded string."""
    if value is None:
        return default
    if not isinstance(value, (str, int, float)):
        return default
    s = str(value).strip()
    return s[:max_len] if s else default

@app.route('/api/logs/ingest', methods=['POST'])
@require_redis_api
def api_ingest_log():
    """Ingest a log entry via HTTP.

    All fields are type-coerced and length-bounded so a remote sender can
    never store oversized or non-string values. If LOG_INGEST_TOKEN is
    configured, the request must carry it in the X-Ingest-Token header.
    Rendering is safe on the client (every dynamic field is HTML-escaped), so
    this is defence in depth - the storage/socket path is not a trust
    boundary.
    """
    if _INGEST_TOKEN:
        provided = (request.headers.get('X-Ingest-Token') or '').strip()
        if provided != _INGEST_TOKEN:
            return jsonify({'error': 'Unauthorized'}), 401

    data = request.get_json(silent=True)
    if not isinstance(data, dict):
        return jsonify({'error': 'No data provided'}), 400

    severity = _clean_str(data.get('severity'), 'info', 32).lower()
    if severity not in _INGEST_SEVERITIES:
        severity = 'info'
    message = _clean_str(data.get('message'), '', 20000)
    if not message:
        return jsonify({'error': 'message required'}), 400

    log_entry = {
        'source': _clean_str(data.get('source'), 'http', 200),
        'source_type': 'http',
        'severity': severity,
        'message': message,
        'hostname': _clean_str(data.get('hostname'), request.remote_addr or 'unknown', 200),
        'program': _clean_str(data.get('program'), 'unknown', 200),
        'timestamp': _clean_str(data.get('timestamp'), None, 64) or None
    }

    log_callback(log_entry)

    return jsonify({'status': 'ok', 'id': log_entry.get('id')})

@app.route('/api/sources')
@require_redis_api
def api_sources():
    """Get unique log sources"""
    return jsonify(redis_client.get_sources())

@app.route('/api/hosts')
@require_redis_api
def api_hosts():
    """Get unique hostnames"""
    return jsonify(redis_client.get_hosts())

@app.route('/api/severities')
@require_redis_api
def api_severities():
    """Get unique severities"""
    return jsonify(redis_client.get_severities())

# Filter API
@app.route('/api/filters', methods=['GET'])
@require_redis_api
def api_get_filters():
    """Get all filters"""
    return jsonify(redis_client.get_filters())

@app.route('/api/filters', methods=['POST'])
@require_redis_api
def api_create_filter():
    """Create a new filter"""
    data = request.get_json()
    if not data:
        return jsonify({'error': 'No data provided'}), 400
    
    filter_id = redis_client.create_filter(data)
    return jsonify({'status': 'ok', 'id': filter_id})

@app.route('/api/filters/<filter_id>', methods=['GET'])
@require_redis_api
def api_get_filter(filter_id):
    """Get a single filter"""
    f = redis_client.get_filter(filter_id)
    if not f:
        return jsonify({'error': 'Filter not found'}), 404
    return jsonify(f)

@app.route('/api/filters/<filter_id>', methods=['PUT'])
@require_redis_api
def api_update_filter(filter_id):
    """Update a filter"""
    data = request.get_json()
    if not data:
        return jsonify({'error': 'No data provided'}), 400
    
    if redis_client.update_filter(filter_id, data):
        return jsonify({'status': 'ok'})
    return jsonify({'error': 'Filter not found'}), 404

@app.route('/api/filters/<filter_id>', methods=['DELETE'])
@require_redis_api
def api_delete_filter(filter_id):
    """Delete a filter"""
    if redis_client.delete_filter(filter_id):
        return jsonify({'status': 'ok'})
    return jsonify({'error': 'Filter not found'}), 404

# Alert API
@app.route('/api/alerts')
@require_redis_api
def api_get_alerts():
    """Get alerts"""
    limit = request.args.get('limit', 50, type=int)
    acknowledged = request.args.get('acknowledged')
    
    if acknowledged is not None:
        acknowledged = acknowledged.lower() == 'true'
    
    alerts = redis_client.get_alerts(limit=limit, acknowledged=acknowledged)
    return jsonify(alerts)

@app.route('/api/alerts/<alert_id>/acknowledge', methods=['POST'])
@require_redis_api
def api_acknowledge_alert(alert_id):
    """Acknowledge an alert"""
    if redis_client.acknowledge_alert(alert_id):
        _push_stats_if_needed()
        return jsonify({'status': 'ok'})
    return jsonify({'error': 'Alert not found'}), 404

@app.route('/api/alerts/acknowledge-all', methods=['POST'])
@require_redis_api
def api_acknowledge_all_alerts():
    """Acknowledge all unacknowledged alerts"""
    count = redis_client.acknowledge_all_alerts()
    if count:
        _push_stats_if_needed()
    return jsonify({'status': 'ok', 'acknowledged': count})

@app.route('/api/alerts/clear', methods=['POST'])
@require_redis_api
def api_clear_alerts():
    """Delete all acknowledged alerts"""
    count = redis_client.clear_acknowledged_alerts()
    if count:
        _push_stats_if_needed()
    return jsonify({'status': 'ok', 'deleted': count})

# Docker API
@app.route('/api/docker/containers')
@require_redis_api
def api_docker_containers():
    """Get Docker containers"""
    if docker_collector:
        return jsonify(docker_collector.get_containers())
    return jsonify([])

@app.route('/api/docker/containers/<container_id>/logs')
@require_redis_api
def api_docker_logs(container_id):
    """Get logs from a specific container"""
    lines = request.args.get('lines', 100, type=int)
    if docker_collector:
        logs = docker_collector.get_container_logs(container_id, lines)
        return jsonify(logs)
    return jsonify([])

# Ollama API
@app.route('/api/ollama/status')
@require_redis_api
def api_ollama_status():
    """Get Ollama status"""
    # Check if a custom host is provided in query params (for testing from settings)
    custom_host = request.args.get('host')
    
    if custom_host:
        # Test with custom host. When AI_PROVIDER is openai (any OpenAI-compatible
        # endpoint such as vLLM / SGLang / omlx), query {host}/v1/models like the
        # analyzer does, instead of the native Ollama /api/tags (which a remote
        # OpenAI-compatible server generally does not expose). Falls back to the
        # native Ollama test if the host turns out to be a real Ollama server.
        import requests as _requests
        import config as _config
        try:
            base = custom_host.rstrip('/')
            url = base + ('/v1/models' if not base.endswith('/v1') else '/models')
            headers = {}
            _api_key = getattr(_config.Config, 'AI_API_KEY', '')
            if _api_key:
                headers['Authorization'] = f'Bearer {_api_key}'
            resp = _requests.get(url, headers=headers, timeout=20)
            resp.raise_for_status()
            data = resp.json()
            models = [m.get('id') or m.get('name') for m in data.get('data', [])]
            models = [m for m in models if m]
            return jsonify({
                'available': True,
                'models': models,
                'current_model': ollama_analyzer.model if ollama_analyzer else '',
                'host': custom_host
            })
        except Exception:
            # Fall back to native Ollama in case the host is a real Ollama server.
            try:
                import ollama as ollama_lib
                client = ollama_lib.Client(host=custom_host)
                response = client.list()
                models = [m['name'] for m in response.get('models', [])]
                return jsonify({
                    'available': True,
                    'models': models,
                    'current_model': ollama_analyzer.model if ollama_analyzer else '',
                    'host': custom_host
                })
            except Exception as e:
                return jsonify({
                    'available': False,
                    'models': [],
                    'error': str(e),
                    'host': custom_host
                })

    # Honor optional force-refresh query parameter to bypass cache
    force = request.args.get('force', '0').lower() in ('1', 'true', 'yes')
    
    # Use configured analyzer
    if ollama_analyzer:
        # Apply the provider/endpoint/model from Settings immediately
        # (bypassing the 2s sync throttle, so a just-saved change is tested
        # right away).  For OpenAI-compatible backends apply_endpoint also
        # rewrites the base_url that /chat/completions actually uses.
        settings = redis_client.get_settings()
        _apply_ai_from_settings(settings)

        available = ollama_analyzer.is_available(force_refresh=force)
        models = ollama_analyzer.get_models(force_refresh=force) if available else []
        return jsonify({
            'available': available,
            'models': models,
            'current_model': ollama_analyzer.model,
            'host': ollama_analyzer.host,
            'provider': ollama_analyzer.provider,
            'base_url': ollama_analyzer.base_url if ollama_analyzer.provider == 'openai' else None
        })
    return jsonify({'available': False, 'models': []})


@app.route('/api/ollama/analyze', methods=['POST'])
@require_redis_api
def api_ollama_analyze():
    """Analyze logs with Ollama"""
    data = request.get_json()
    _sync_analyzer_settings()
    
    if not ollama_analyzer or not ollama_analyzer.is_available():
        return jsonify({'error': 'Ollama not available'}), 503
    
    if 'log_id' in data:
        log = redis_client.get_log(data['log_id'])
        if not log:
            return jsonify({'error': 'Log not found'}), 404
        result = ollama_analyzer.analyze_log(log)
        
        # Store every single analysis in history (success and failure alike).
        if result.get('success'):
            redis_client.store_analysis_history({
                'type': 'single',
                'logs_analyzed': 1,
                'log_ids': [log.get('id') or data.get('log_id')],
                'analysis': result.get('analysis', {}),
                'log_source': log.get('source', 'unknown'),
                'log_severity': log.get('severity', 'info')
            })
    elif 'logs' in data:
        result = ollama_analyzer.analyze_logs_batch(data['logs'])
        
        # Store every batch analysis in history (success and failure alike).
        if result.get('success'):
            redis_client.store_analysis_history({
                'type': 'batch',
                'logs_analyzed': result.get('logs_analyzed', len(data['logs'])),
                'log_ids': [l.get('id') for l in data['logs'] if l.get('id')],
                'analysis': result.get('analysis', {})
            })
    else:
        return jsonify({'error': 'No log_id or logs provided'}), 400
    
    return jsonify(result)

@app.route('/api/ai-history')
@require_redis_api
def api_ai_history():
    """Get AI analysis history page (newest-first, ``limit`` per page at
    ``offset``) plus the live total for pagination."""
    limit = min(request.args.get('limit', 100, type=int), 500)
    offset = max(request.args.get('offset', 0, type=int), 0)
    history = redis_client.get_analysis_history(limit=limit, offset=offset)
    return jsonify({
        'history': history,
        'total': redis_client.get_analysis_history_count(),
        'offset': offset,
        'limit': limit
    })

@app.route('/api/ai-history/stats')
@require_redis_api
def api_ai_history_stats():
    """Aggregate status counts across ALL live analysis-history entries
    (cached ~60s server-side)."""
    return jsonify(redis_client.get_ai_history_stats())

@app.route('/api/ai-history/<history_id>')
@require_redis_api
def api_ai_history_entry(history_id):
    """Get a single AI analysis history entry"""
    entry = redis_client.get_analysis_history_entry(history_id)
    if not entry:
        return jsonify({'error': 'History entry not found'}), 404
    return jsonify(entry)

@app.route('/api/ai-history/<history_id>', methods=['DELETE'])
@require_redis_api
def api_ai_history_delete(history_id):
    """Delete an AI analysis history entry"""
    if redis_client.delete_analysis_history(history_id):
        return jsonify({'status': 'ok'})
    return jsonify({'error': 'History entry not found'}), 404

@app.route('/api/ai-history', methods=['DELETE'])
@require_redis_api
def api_ai_history_clear_all():
    """Clear all AI analysis history"""
    count = redis_client.clear_all_analysis_history()
    return jsonify({'status': 'ok', 'deleted': count})

@app.route('/api/analysis/reanalyze', methods=['POST'])
@require_redis_api
def api_analysis_reanalyze():
    """Re-analyze the logs that produced a given AI history entry, if they
    still exist. Returns the new analysis (a fresh history entry is stored)."""
    data = request.get_json() or {}
    history_id = data.get('history_id')
    if not history_id:
        return jsonify({'error': 'history_id required'}), 400

    if not ollama_analyzer:
        return jsonify({'error': 'AI not available'}), 503
    _sync_analyzer_settings()

    entry = redis_client.get_analysis_history_entry(history_id)
    if not entry:
        return jsonify({'error': 'History entry not found'}), 404

    log_ids = entry.get('log_ids') or []
    if not log_ids:
        return jsonify({'reanalyzed': False,
                        'msg': 'This history record has no stored log IDs, so it cannot be re-analyzed.',
                        'available': 0})

    logs = redis_client.get_logs_by_ids(log_ids)
    if not logs:
        return jsonify({'reanalyzed': False,
                        'msg': 'The logs for this analysis are no longer available (expired or purged).',
                        'available': 0})

    entry_type = entry.get('type', 'auto')
    if entry_type == 'single' or len(log_ids) == 1:
        log = logs[0]
        result = ollama_analyzer.analyze_log(log)
        new_type = 'single'
    else:
        result = ollama_analyzer.analyze_logs_batch(logs)
        new_type = 'batch'

    if result.get('success'):
        new_analysis = result.get('analysis', {})
        if not _is_failed_analysis(new_analysis):
            # Re-analysis SUCCEEDED -> update the original failed record in place
            # with the successful result, so it becomes a normal record (and no
            # longer shows a re-analyse button). Also mark the logs analyzed so
            # the automatic batch queue does not analyse them a second time.
            redis_client.update_analysis_history(history_id, {
                'type': new_type,
                'logs_analyzed': len(logs),
                'log_ids': [l.get('id') for l in logs if l.get('id')],
                'analysis': new_analysis
            })
            redis_client.mark_logs_analyzed(logs, str(new_analysis))
            try:
                redis_client.purge_stale_failed_history(limit=30)
            except Exception:
                pass
            return jsonify({'reanalyzed': True, 'updated': True, 'available': len(logs), 'analysis': new_analysis})
        # Re-analysis ALSO failed -> leave the original record unchanged (do not
        # update, do not create a new one). Just report that it is still failed.
        return jsonify({'reanalyzed': True, 'updated': False, 'available': len(logs), 'analysis': new_analysis, 'msg': 'Re-analysis still failed; original record unchanged.'})
    # The AI had no usable response (timeout / connection error / bad reply).
    # Report it clearly so the record stays re-analysable once the AI is back.
    msg = 'AI did not respond: ' + str(result.get('error', 'unknown error'))
    print(f"[App] Re-analysis of {history_id} failed: {msg}")
    return jsonify({'reanalyzed': False, 'msg': msg, 'available': len(logs)}), 200


@app.route('/api/ollama/chat', methods=['POST'])
@require_redis_api
def api_ollama_chat():
    """Chat with AI about logs"""
    data = request.get_json()
    _sync_analyzer_settings()
    
    if not ollama_analyzer or not ollama_analyzer.is_available():
        return jsonify({'error': 'Ollama not available'}), 503
    
    message = data.get('message', '')
    context = data.get('context', '')
    
    response = ollama_analyzer.chat(message, context)
    return jsonify({'response': response})

# Settings API
@app.route('/api/settings', methods=['GET'])
@require_redis_api
def api_get_settings():
    """Get settings"""
    return jsonify(redis_client.get_settings())

@app.route('/api/settings', methods=['POST'])
@require_redis_api
def api_save_settings():
    """Save settings.

    Tuning values (analysis_interval / max_logs_per_analysis /
    batch_sample_limit / log_retention_hours) are clamped to sane ranges and
    take effect immediately - the periodic-analysis job is rescheduled live
    and the other knobs are re-read from Redis on every use.
    """
    data = request.get_json()
    if not isinstance(data, dict):
        return jsonify({'error': 'No data provided'}), 400

    # Normalise / clamp tuning integers so a bad UI value can never set a
    # 0-minute interval or a negative retention.
    def _clamp_int(value, lo, hi, default):
        try:
            v = int(value)
        except (TypeError, ValueError):
            return default
        return max(lo, min(hi, v))

    if 'analysis_interval' in data:
        data['analysis_interval'] = _clamp_int(data['analysis_interval'], 1, 1440, Config.ANALYSIS_INTERVAL_MINUTES)
    if 'max_logs_per_analysis' in data:
        data['max_logs_per_analysis'] = _clamp_int(data['max_logs_per_analysis'], 10, 5000, Config.MAX_LOGS_PER_ANALYSIS)
    if 'batch_sample_limit' in data:
        data['batch_sample_limit'] = _clamp_int(data['batch_sample_limit'], 1, 5000, 200)
    if 'log_retention_hours' in data:
        data['log_retention_hours'] = _clamp_int(data['log_retention_hours'], 1, 24 * 365, Config.LOG_RETENTION_HOURS)
    # Health-watchdog knobs (also live-tunable from General Settings)
    if 'health_watch_minutes' in data:
        data['health_watch_minutes'] = _clamp_int(data['health_watch_minutes'], 1, 1440, _HEALTH_WATCH_MINUTES)
    if 'health_backlog_warn' in data:
        data['health_backlog_warn'] = _clamp_int(data['health_backlog_warn'], 0, 1000000, _HEALTH_BACKLOG_WARN_DEFAULT)
    if 'health_alert_cooldown_min' in data:
        data['health_alert_cooldown_min'] = _clamp_int(data['health_alert_cooldown_min'], 1, 1440, _HEALTH_COOLDOWN_MIN_DEFAULT)
    if 'health_daily_summary' in data:
        data['health_daily_summary'] = bool(data['health_daily_summary'])

    redis_client.save_settings(data)

    # Reconfigure services
    if data.get('telegram_bot_token') and data.get('telegram_chat_id'):
        telegram_notifier.configure(
            data['telegram_bot_token'],
            data['telegram_chat_id']
        )

    if ollama_analyzer:
        # Fall back to env-configured values when the saved ones are empty,
        # so a blank model/endpoint field in the UI can't silently break AI
        # analysis.  Provider / endpoint / model are applied live from the
        # just-saved settings (apply_endpoint also updates base_url for
        # OpenAI-compatible backends).
        _apply_ai_from_settings(redis_client.get_settings())
        if 'batch_sample_limit' in data:
            ollama_analyzer.batch_sample_limit = data['batch_sample_limit']

    # Apply live: reschedule the periodic-analysis job with the new interval.
    # (Retention / batch size / sample limit are read from Redis on each use,
    # so they need no further action.)
    if 'analysis_interval' in data:
        _reschedule_analysis_job()
    if 'health_watch_minutes' in data:
        _reschedule_health_job()

    _push_stats_if_needed()
    return jsonify({'status': 'ok'})

@app.route('/api/telegram/test', methods=['POST'])
@require_redis_api
def api_telegram_test():
    """Test Telegram connection"""
    data = request.get_json() or {}
    
    bot_token = data.get('bot_token') or telegram_notifier.bot_token
    chat_id = data.get('chat_id') or telegram_notifier.chat_id
    
    if bot_token and chat_id:
        telegram_notifier.configure(bot_token, chat_id)
    
    success, message = telegram_notifier.test_connection()
    return jsonify({'success': success, 'message': message})

# ==================== SYSLOG DIAGNOSTICS API ====================

@app.route('/api/syslog/diagnostics')
@login_required
def api_syslog_diagnostics():
    """Get syslog receiver diagnostics and client information"""
    if not syslog_receiver:
        return jsonify({
            'error': 'Syslog receiver not initialized',
            'receiver_running': False,
            'clients': []
        }), 500
    
    diagnostics = syslog_receiver.get_client_diagnostics()
    return jsonify(diagnostics)

@app.route('/api/syslog/clients/<client_ip>', methods=['DELETE'])
@login_required
def api_delete_syslog_client(client_ip):
    """Manually remove a connected-client record."""
    if not syslog_receiver:
        return jsonify({'error': 'Syslog receiver not initialized'}), 500
    ok = syslog_receiver.delete_client(client_ip)
    return jsonify({'deleted': ok})

@app.route('/api/syslog/test-receive', methods=['POST'])
@login_required
def api_syslog_test_receive():
    """
    Send a test syslog message to verify the receiver is working.
    This sends a UDP message to the local syslog port.
    """
    import socket as test_socket
    
    try:
        test_msg = f"<14>Jan  1 00:00:00 LogRadarAI-test test[9999]: Test message from LogRadarAI diagnostics at {datetime.now(timezone.utc).isoformat()}"
        
        sock = test_socket.socket(test_socket.AF_INET, test_socket.SOCK_DGRAM)
        sock.sendto(test_msg.encode('utf-8'), ('127.0.0.1', Config.SYSLOG_PORT))
        sock.close()
        
        return jsonify({
            'success': True,
            'message': f'Test message sent to UDP port {Config.SYSLOG_PORT}. Check if it appears in recent logs.'
        })
    except Exception as e:
        return jsonify({
            'success': False,
            'message': f'Failed to send test message: {str(e)}'
        }), 500

# ==================== SOCKETIO EVENTS ====================

@socketio.on('connect')
def handle_connect():
    """Handle client connection.

    Only authenticated (logged-in) browser sessions may open a socket: every
    realtime event (new_log / new_alert / analysis_complete / stats) is
    broadcast to all connected clients, and an anonymous cross-site socket
    would otherwise receive the whole log stream.  Returning False rejects the
    handshake (the client keeps retrying with its cookie once logged in).
    """
    try:
        if not current_user.is_authenticated:
            # Anonymous visitors (e.g. the login page) get rejected; keep this
            # silent - the client retries periodically and a print per attempt
            # would spam the logs.
            if Config.DEBUG:
                print("[SocketIO] Rejected anonymous connection")
            return False
    except Exception as e:
        if Config.DEBUG:
            print(f"[SocketIO] Connection auth error: {e}")
        return False
    print("[SocketIO] Client connected")
    emit('connected', {'status': 'ok'})

@socketio.on('disconnect')
def handle_disconnect():
    """Handle client disconnection"""
    print(f"[SocketIO] Client disconnected")

@socketio.on('subscribe_logs')
def handle_subscribe_logs():
    """Subscribe to log updates"""
    print(f"[SocketIO] Client subscribed to logs")

# ==================== USER MANAGEMENT API ====================

@app.route('/api/users', methods=['GET'])
@login_required
@require_redis_api
def api_get_users():
    """Get all users (admin only)"""
    if not current_user.is_admin:
        return jsonify({'error': 'Access denied'}), 403
    users = redis_client.get_all_users()
    # Remove password hashes from response
    for user in users:
        user.pop('password_hash', None)
    return jsonify(users)

@app.route('/api/users', methods=['POST'])
@login_required
@require_redis_api
def api_create_user():
    """Create a new user (admin only)"""
    if not current_user.is_admin:
        return jsonify({'error': 'Access denied'}), 403
    
    data = request.get_json()
    if not data:
        return jsonify({'error': 'No data provided'}), 400
    
    username = data.get('username', '').strip()
    password = data.get('password', '')
    email = data.get('email', '').strip()
    role = data.get('role', 'user')
    
    if not username or not password:
        return jsonify({'error': 'Username and password are required'}), 400
    
    if len(password) < 6:
        return jsonify({'error': 'Password must be at least 6 characters'}), 400
    
    # Check if username already exists
    if redis_client.get_user_by_username(username):
        return jsonify({'error': 'Username already exists'}), 400
    
    user_data = {
        'username': username,
        'password_hash': generate_password_hash(password),
        'email': email,
        'role': role
    }
    
    user_id = redis_client.create_user(user_data)
    return jsonify({'status': 'ok', 'id': user_id})

@app.route('/api/users/<user_id>', methods=['PUT'])
@login_required
@require_redis_api
def api_update_user(user_id):
    """Update a user (admin only, or self)"""
    if not current_user.is_admin and current_user.id != user_id:
        return jsonify({'error': 'Access denied'}), 403
    
    data = request.get_json()
    if not data:
        return jsonify({'error': 'No data provided'}), 400
    
    user = redis_client.get_user(user_id)
    if not user:
        return jsonify({'error': 'User not found'}), 404
    
    # Update allowed fields
    if 'email' in data:
        user['email'] = data['email'].strip()
    
    if 'password' in data and data['password']:
        if len(data['password']) < 6:
            return jsonify({'error': 'Password must be at least 6 characters'}), 400
        user['password_hash'] = generate_password_hash(data['password'])
    
    # Only admin can change role
    if current_user.is_admin and 'role' in data:
        user['role'] = data['role']
    
    redis_client.update_user(user_id, user)
    return jsonify({'status': 'ok'})

@app.route('/api/users/<user_id>', methods=['DELETE'])
@login_required
@require_redis_api
def api_delete_user(user_id):
    """Delete a user (admin only)"""
    if not current_user.is_admin:
        return jsonify({'error': 'Access denied'}), 403
    
    # Prevent deleting yourself
    if current_user.id == user_id:
        return jsonify({'error': 'Cannot delete your own account'}), 400
    
    if redis_client.delete_user(user_id):
        return jsonify({'status': 'ok'})
    return jsonify({'error': 'User not found'}), 404

@app.route('/api/users/current', methods=['GET'])
@login_required
@require_redis_api
def api_current_user():
    """Get current user info"""
    return jsonify({
        'id': current_user.id,
        'username': current_user.username,
        'email': current_user.email,
        'role': current_user.role,
        'is_admin': current_user.is_admin
    })

# ==================== STARTUP ====================

_services_initialized = False

def create_app():
    """Application factory"""
    global _services_initialized
    
    # Ensure templates directory exists
    os.makedirs(os.path.join(app.root_path, 'templates'), exist_ok=True)
    os.makedirs(os.path.join(app.root_path, 'static', 'css'), exist_ok=True)
    os.makedirs(os.path.join(app.root_path, 'static', 'js'), exist_ok=True)
    
    # Initialize services once (for flask run)
    if not _services_initialized:
        # In debug mode, only initialize in the reloader child process (WERKZEUG_RUN_MAIN)
        if Config.DEBUG and os.environ.get('WERKZEUG_RUN_MAIN') != 'true':
            print("[App] Debug reloader detected - delaying service initialization to child process")
        else:
            init_services()
            _services_initialized = True
    
    return app

# Auto-initialize services when module is loaded (for flask run)
with app.app_context():
    if not _services_initialized:
        # In debug mode, only initialize in the reloader child process (WERKZEUG_RUN_MAIN)
        if Config.DEBUG and os.environ.get('WERKZEUG_RUN_MAIN') != 'true':
            print("[App] Debug reloader detected - delaying service initialization to child process")
        else:
            init_services()
            _services_initialized = True

if __name__ == '__main__':
    # Run the app with socketio
    socketio.run(
        app,
        host='0.0.0.0',
        port=5059,
        debug=Config.DEBUG,
        allow_unsafe_werkzeug=True
    )
