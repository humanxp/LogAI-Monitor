# Copyright (C) 2026 Fotios Tsiadimos
# SPDX-License-Identifier: GPL-3.0-or-later

import redis
import json
import time
import os
import threading
from datetime import datetime, timezone
from typing import List, Dict, Optional, Any
from config import Config


class RedisClient:
    """Redis client for log storage and filter management"""

    # How long settings / filters are cached in-process.  The receive path
    # (per-message) and the Docker watcher used to hit Redis for settings on
    # every single line; a few seconds of staleness is invisible to users and
    # turns ~N round-trips/message into 1 every T seconds.
    CACHE_TTL = 5.0
    # get_stats() is polled by every open dashboard roughly once per second;
    # serve a short in-process cache instead of recomputing it each time.
    STATS_CACHE_TTL = 2.0

    def __init__(self):
        self.client = redis.Redis(
            host=Config.REDIS_HOST,
            port=Config.REDIS_PORT,
            db=Config.REDIS_DB,
            password=Config.REDIS_PASSWORD,
            decode_responses=True
        )
        self._settings_cache = {'ts': 0.0, 'value': None}
        self._filters_cache = {'ts': 0.0, 'value': None}
        self._stats_cache = {'ts': 0.0, 'value': None}
        self._cache_lock = threading.Lock()
        # Whether the logs:unanalyzed index has been backfilled once after an
        # upgrade (older entries stored before the index existed).
        self._unanalyzed_backfilled = False
        self._backfill_lock = threading.Lock()

    def ping(self) -> bool:
        """Check Redis connection"""
        try:
            return self.client.ping()
        except Exception:
            return False

    # ------------------------------------------------------------------
    # small helpers
    # ------------------------------------------------------------------
    def _scan_keys(self, pattern: str):
        """Yield keys matching ``pattern`` via SCAN (never blocks Redis the way
        KEYS does on a large keyspace)."""
        try:
            return list(self.client.scan_iter(match=pattern, count=500))
        except Exception:
            # Fall back for exotic cases (older servers / proxies)
            return list(self.client.keys(pattern))

    @staticmethod
    def _jsonify(mapping: Dict) -> Dict:
        """Encode values for hash storage (booleans/objects as JSON)."""
        return {k: (json.dumps(v) if isinstance(v, (dict, list, bool)) else str(v))
                for k, v in mapping.items()}

    # ==================== LOG OPERATIONS ====================

    def store_log(self, log_entry: Dict) -> str:
        """Store a log entry.

        Every Redis write for one message (hash, timeline, unanalyzed index,
        host/source/severity indexes, TTL) is sent in ONE pipeline round trip
        instead of ~6 sequential ones.
        """
        log_id = f"log:{int(time.time() * 1000000)}"
        log_entry['id'] = log_id
        log_entry['timestamp'] = log_entry.get('timestamp', datetime.now(timezone.utc).isoformat())
        log_entry['analyzed'] = False

        # TTL follows the retention actually configured in Settings (which the
        # UI shows and cleanup honours), not the env default baked in at boot.
        # Previously the hash TTL came from the env value while cleanup/UI used
        # the Settings value, so logs "disappeared" far earlier than the UI's
        # declared retention.
        ttl_seconds = self._retention_seconds()

        source = log_entry.get('source', 'unknown')
        hostname = log_entry.get('hostname', log_entry.get('source', 'unknown'))
        severity = log_entry.get('severity', 'info')
        now = time.time()

        pipe = self.client.pipeline()
        pipe.hset(log_id, mapping=self._jsonify(log_entry))
        pipe.zadd('logs:timeline', {log_id: now})
        pipe.zadd('logs:unanalyzed', {log_id: now})
        pipe.sadd(f'logs:source:{source}', log_id)
        pipe.sadd(f'logs:host:{hostname}', log_id)
        pipe.sadd(f'logs:severity:{severity}', log_id)
        if ttl_seconds and ttl_seconds > 0:
            pipe.expire(log_id, ttl_seconds)
        pipe.execute()
        return log_id

    def _retention_seconds(self) -> int:
        """Retention (seconds) from Settings when available, else env default."""
        try:
            settings = self.get_settings()
            hours = int(settings.get('log_retention_hours') or Config.LOG_RETENTION_HOURS)
        except Exception:
            hours = Config.LOG_RETENTION_HOURS
        return max(0, hours) * 3600

    def get_log(self, log_id: str) -> Optional[Dict]:
        """Get a single log entry"""
        data = self.client.hgetall(log_id)
        if not data:
            return None

        # Parse JSON fields
        for key in ['analyzed']:
            if key in data:
                try:
                    data[key] = json.loads(data[key])
                except:
                    pass
        return data

    def get_logs(self, limit: int = 100, offset: int = 0, source: str = None,
                 severity: str = None, search: str = None, start_time: float = None,
                 end_time: float = None, host: str = None) -> List[Dict]:
        """Get logs with optional filtering.

        Two paths for speed and correctness:
        * host/source/severity filter given -> use the Redis index sets, which
          contain ALL matching ids regardless of age (a host whose logs were
          pushed out of the newest window must still be queryable);
        * no filter -> only the newest bounded window of the timeline is
          considered (real-time recent-logs viewer).
        Hashes are fetched with a redis pipeline either way.
        """
        ids = []
        if start_time and end_time:
            ids = self.client.zrevrangebyscore('logs:timeline', end_time, start_time)
        elif host and host.strip() or source and source.strip() or severity and severity.strip():
            # Index-set based: exact matches at any age
            sets = []
            if host and host.strip():
                sets.append(self.client.smembers(f'logs:host:{host}'))
            if source and source.strip():
                sets.append(self.client.smembers(f'logs:source:{source}'))
            if severity and severity.strip():
                sets.append(self.client.smembers(f'logs:severity:{severity}'))
            ids = list(sets[0])
            for s in sets[1:]:
                ids = [lid for lid in ids if lid in s]
        else:
            # No filter: recent-logs viewer over a bounded newest window
            window = offset + limit + 500
            ids = self.client.zrevrange('logs:timeline', 0, window - 1)

        if not ids:
            return []

        # Newest-first by timeline score (pipelined zscores)
        pipe = self.client.pipeline()
        for lid in ids:
            pipe.zscore('logs:timeline', lid)
        scores = pipe.execute()
        ordered = sorted(zip(ids, scores), key=lambda p: (p[1] or 0), reverse=True)
        # Fetch at most what the page (+ search slack) can use
        need = offset + limit + (500 if search and search.strip() else 0)
        ordered_ids = [lid for lid, _ in ordered][:need]

        # Fetch hashes (pipelined) and apply search filter
        logs = self._fetch_logs_pipelined(ordered_ids)
        if search and search.strip():
            search_lower = search.lower().strip()
            filtered = []
            for log in logs:
                if (search_lower in (log.get('message') or '').lower() or
                    search_lower in (log.get('source') or '').lower() or
                    search_lower in (log.get('hostname') or '').lower() or
                    search_lower in (log.get('program') or '').lower()):
                    filtered.append(log)
            logs = filtered

        # Apply pagination
        return logs[offset:offset + limit]

    def _fetch_logs_pipelined(self, log_ids) -> List[Dict]:
        """Fetch log hashes with a redis pipeline (chunked) instead of one
        sequential hgetall per id."""
        if not log_ids:
            return []
        CHUNK = 800
        out = []
        for i in range(0, len(log_ids), CHUNK):
            chunk = log_ids[i:i + CHUNK]
            pipe = self.client.pipeline()
            for lid in chunk:
                pipe.hgetall(lid)
            results = pipe.execute()
            for lid, data in zip(chunk, results):
                if not data:
                    continue
                data = dict(data)
                if 'analyzed' in data:
                    try:
                        data['analyzed'] = json.loads(data['analyzed'])
                    except:
                        pass
                out.append(data)
        return out

    def get_logs_count(self) -> int:
        """Get total log count"""
        return self.client.zcard('logs:timeline')

    def get_filtered_log_count(self, source: str = None, host: str = None,
                               severity: str = None) -> int:
        """Number of log ids that match the given host/source/severity index
        filters (exact-match, like get_logs' filtered path).

        Used by /api/logs so the UI paginator counts the FILTERED dataset
        (otherwise selecting one device would still report the whole-database
        total and paginate through unrelated pages).
        """
        sets = []
        if host and str(host).strip():
            sets.append(self.client.smembers(f'logs:host:{host}'))
        if source and str(source).strip():
            sets.append(self.client.smembers(f'logs:source:{source}'))
        if severity and str(severity).strip():
            sets.append(self.client.smembers(f'logs:severity:{severity}'))
        if not sets:
            return self.get_logs_count()
        base = set(sets[0])
        for s in sets[1:]:
            base &= s
        return len(base)

    # ------------------------------------------------------------------
    # Unanalyzed-log index.
    #
    # ``logs:unanalyzed`` is a zset (score = arrival time) maintained alongside
    # the timeline: every stored log is added, and it is removed the moment the
    # log is marked analyzed / deleted / cleaned up.  The old implementation
    # scanned the WHOLE timeline and did one hgetall per entry until it found
    # enough unanalyzed ones - O(total logs) Redis round-trips every batch.
    # ------------------------------------------------------------------
    def _ensure_unanalyzed_index(self):
        """One-time backfill after an upgrade: entries stored before this index
        existed are not in it yet, so scan the timeline once and rebuild it."""
        if self._unanalyzed_backfilled:
            return
        with self._backfill_lock:
            if self._unanalyzed_backfilled:
                return
            try:
                total = self.client.zcard('logs:timeline') or 0
                indexed = self.client.zcard('logs:unanalyzed') or 0
                if total > 0 and indexed == 0:
                    print("[Redis] Backfilling unanalyzed index...")
                    all_ids = self.client.zrange('logs:timeline', 0, -1)
                    unanalyzed = []
                    CHUNK = 2000
                    for i in range(0, len(all_ids), CHUNK):
                        chunk = all_ids[i:i + CHUNK]
                        pipe = self.client.pipeline()
                        for lid in chunk:
                            pipe.hget(lid, 'analyzed')
                        results = pipe.execute()
                        for lid, val in zip(chunk, results):
                            if str(val or 'false').strip().lower() != 'true':
                                unanalyzed.append(lid)
                    if unanalyzed:
                        # Fetch real timeline scores (oldest-first ordering)
                        pipe = self.client.pipeline()
                        for lid in unanalyzed:
                            pipe.zscore('logs:timeline', lid)
                        scores = pipe.execute()
                        pipe = self.client.pipeline()
                        for lid, s in zip(unanalyzed, scores):
                            if s is not None:
                                pipe.zadd('logs:unanalyzed', {lid: float(s)})
                        pipe.execute()
                    print(f"[Redis] Unanalyzed index backfilled: {len(unanalyzed)} pending")
            except Exception as e:
                print(f"[Redis] Unanalyzed backfill failed (will retry): {e}")
                return
            self._unanalyzed_backfilled = True

    def get_unanalyzed_logs(self, limit: int = 100) -> List[Dict]:
        """Get logs that haven't been analyzed yet.

        Uses the ``logs:unanalyzed`` index (oldest-first) so a batch fetch is a
        bounded zrange + pipelined hgetalls regardless of timeline size.
        """
        self._ensure_unanalyzed_index()
        log_ids = self.client.zrange('logs:unanalyzed', 0, limit - 1)
        if not log_ids:
            return []
        fetched = self._fetch_logs_pipelined(log_ids)
        logs = []
        for lid, log in zip(log_ids, fetched):
            # skip ids whose hash expired between index and fetch
            analyzed = str(log.get('analyzed', 'false')).strip().lower()
            if analyzed != 'true':
                log['id'] = lid
                logs.append(log)
            if len(logs) >= limit:
                break
        return logs

    def get_logs_by_ids(self, log_ids):
        """Return the logs from ``log_ids`` that still exist, preserving order and
        attaching each log's id. Missing/expired ids are skipped."""
        if not log_ids:
            return []
        logs = []
        for lid in log_ids:
            log = self.get_log(lid)
            if log:
                log['id'] = lid
                logs.append(log)
        return logs

    def mark_log_analyzed(self, log_id: str, analysis: str):
        """Mark a single log as analyzed and store the analysis."""
        pipe = self.client.pipeline()
        pipe.hset(log_id, 'analyzed', 'true')
        pipe.hset(log_id, 'analysis', analysis)
        pipe.zrem('logs:unanalyzed', log_id)
        pipe.execute()

    def mark_logs_analyzed(self, logs: List[Dict], analysis: str):
        """Mark many logs analyzed in ONE pipeline (batch path)."""
        if not logs:
            return
        pipe = self.client.pipeline()
        for log in logs:
            lid = log.get('id')
            if not lid:
                continue
            pipe.hset(lid, 'analyzed', 'true')
            pipe.hset(lid, 'analysis', analysis)
            pipe.zrem('logs:unanalyzed', lid)
        pipe.execute()

    def get_sources(self) -> List[str]:
        """Get all unique log sources"""
        return [k.replace('logs:source:', '') for k in self._scan_keys('logs:source:*')]

    def get_severities(self) -> List[str]:
        """Get all unique severities"""
        return [k.replace('logs:severity:', '') for k in self._scan_keys('logs:severity:*')]

    def get_hosts(self) -> List[str]:
        """Get all unique hostnames"""
        return sorted([k.replace('logs:host:', '') for k in self._scan_keys('logs:host:*')])

    def cleanup_old_logs(self, retention_hours: int = None):
        """Remove logs older than retention period and record cleanup status.

        Also purges DEAD ids: log hashes expire via their Redis TTL, which does
        NOT remove them from the timeline / unanalyzed / index collections -
        without this they keep growing with stale entries.  Everything is
        executed in pipelined batches.
        """
        # Determine effective retention (hours) to use
        try:
            settings = self.get_settings()
            used_retention = int(retention_hours if retention_hours is not None else settings.get('log_retention_hours', Config.LOG_RETENTION_HOURS))
        except Exception:
            used_retention = int(retention_hours if retention_hours is not None else Config.LOG_RETENTION_HOURS)

        cutoff = time.time() - (used_retention * 3600)
        old_logs = self.client.zrangebyscore('logs:timeline', 0, cutoff)

        removed = 0
        CHUNK = 2000
        # Bulk-fetch the hashes of the logs being removed (to learn their indexes)
        for i in range(0, len(old_logs), CHUNK):
            chunk = old_logs[i:i + CHUNK]
            pipe = self.client.pipeline()
            for log_id in chunk:
                pipe.hgetall(log_id)
            data_list = pipe.execute()
            removals = []
            for log_id, data in zip(chunk, data_list):
                if not data:
                    removals.append((log_id, None, None, None))
                    continue
                data = dict(data)
                removals.append((
                    log_id,
                    data.get('source', 'unknown'),
                    data.get('severity', 'info'),
                    data.get('hostname') or data.get('source') or 'unknown',
                ))
            if not removals:
                continue
            pipe = self.client.pipeline()
            for log_id, source, sev, host in removals:
                if source is not None:
                    pipe.srem(f'logs:source:{source}', log_id)
                    pipe.srem(f'logs:severity:{sev}', log_id)
                    pipe.srem(f'logs:host:{host}', log_id)
                pipe.delete(log_id)
                pipe.zrem('logs:timeline', log_id)
                pipe.zrem('logs:unanalyzed', log_id)
            pipe.execute()
            removed += len(removals)

        # Phase 2: drop timeline/unanalyzed members whose hash already expired (TTL)
        all_ids = self.client.zrange('logs:timeline', 0, -1)
        removed_dead = 0
        for i in range(0, len(all_ids), CHUNK):
            chunk = all_ids[i:i + CHUNK]
            pipe = self.client.pipeline()
            for lid in chunk:
                pipe.exists(lid)
            results = pipe.execute()
            dead = [lid for lid, ok in zip(chunk, results) if not ok]
            if dead:
                self.client.zrem('logs:timeline', *dead)
                self.client.zrem('logs:unanalyzed', *dead)
                removed_dead += len(dead)

        # Phase 3: purge stale ids from the index sets (they only reference
        # hashes, so a missing hash means the id is dead).
        for pattern in ('logs:host:*', 'logs:source:*', 'logs:severity:*'):
            for key in self._scan_keys(pattern):
                members = list(self.client.smembers(key))
                for j in range(0, len(members), CHUNK):
                    mchunk = members[j:j + CHUNK]
                    pipe = self.client.pipeline()
                    for m in mchunk:
                        pipe.exists(m)
                    results = pipe.execute()
                    dead = [m for m, ok in zip(mchunk, results) if not ok]
                    if dead:
                        self.client.srem(key, *dead)

        total_removed = removed + removed_dead
        # Record last cleanup info so we can inspect it later (useful in Docker)
        try:
            now = datetime.now(timezone.utc).isoformat()
            self.client.set('cleanup:last_run', now)
            self.client.set('cleanup:last_removed', total_removed)
            self.client.set('cleanup:last_used_retention', used_retention)
        except Exception:
            # Don't fail cleanup if recording status fails
            pass

        return total_removed

    def get_cleanup_status(self):
        """Return cleanup last run info and current timeline count"""
        try:
            last_run = self.client.get('cleanup:last_run')
            last_removed = self.client.get('cleanup:last_removed')
            return {
                'last_run': last_run,
                'last_removed': int(last_removed) if last_removed is not None else 0,
                'timeline_count': self.get_logs_count()
            }
        except Exception:
            return {
                'last_run': None,
                'last_removed': 0,
                'timeline_count': self.get_logs_count()
            }

    def delete_logs_by_source(self, source_ip: str) -> int:
        """Permanently delete every stored log received from a given source IP.

        Uses the ``logs:source:<ip>`` index so only logs FROM this sender are
        removed - other hosts (even ones sharing the same parsed hostname) are
        untouched. Runs in pipelined batches.
        """
        ids = list(self.client.smembers('logs:source:' + source_ip))
        removed = 0
        CHUNK = 1000
        for i in range(0, len(ids), CHUNK):
            chunk = ids[i:i + CHUNK]
            pipe = self.client.pipeline()
            for lid in chunk:
                pipe.hgetall(lid)
            data_list = pipe.execute()
            removals = []
            for lid, data in zip(chunk, data_list):
                if not data:
                    continue
                data = dict(data)
                removals.append((
                    lid,
                    data.get('hostname') or data.get('source') or source_ip,
                    data.get('severity') or 'info',
                ))
            if not removals:
                continue
            pipe = self.client.pipeline()
            for lid, host, sev in removals:
                pipe.srem(f'logs:host:{host}', lid)
                pipe.srem(f'logs:severity:{sev}', lid)
                pipe.srem(f'logs:source:{source_ip}', lid)
                pipe.zrem('logs:timeline', lid)
                pipe.zrem('logs:unanalyzed', lid)
                pipe.delete(lid)
            pipe.execute()
            removed += len(removals)
        return removed

    def clear_syslog_clients(self) -> int:
        """Remove ALL Connected-Clients tracking records.

        Called together with clearing the whole log database so the client
        list does not keep stale stats after every log was deleted. (Clients
        simply re-appear the next time they send a message.)
        """
        keys = self._scan_keys('syslog:client:*')
        keys.append('syslog:clients:index')
        keys = list(dict.fromkeys(keys))
        removed = 0
        if keys:
            CHUNK = 1000
            for i in range(0, len(keys), CHUNK):
                self.client.delete(*keys[i:i + CHUNK])
            removed = len(keys)
        return removed

    def clear_all_logs(self) -> int:
        """Delete all logs from the database (fast).

        Bulk-deletes the log hashes in chunks instead of one round-trip per
        log, and skips per-log index bookkeeping entirely because every index
        key is removed afterwards anyway. With ~100k logs the old code took
        minutes (7 Redis calls per log); this finishes in seconds.
        """
        log_ids = self.client.zrange('logs:timeline', 0, -1)
        count = len(log_ids)
        CHUNK = 5000
        for i in range(0, len(log_ids), CHUNK):
            self.client.delete(*log_ids[i:i + CHUNK])
        # Remove timeline and all index keys
        self.client.delete('logs:timeline')
        self.client.delete('logs:unanalyzed')
        for pattern in ('logs:source:*', 'logs:host:*', 'logs:severity:*'):
            keys = self._scan_keys(pattern)
            if keys:
                self.client.delete(*keys)
        return count

    # ==================== FILTER OPERATIONS ====================

    def _cached(self, name: str, loader):
        """Generic tiny TTL cache (thread-safe enough for our use)."""
        with self._cache_lock:
            cache = getattr(self, '_' + name + '_cache')
            now = time.time()
            if cache['value'] is not None and (now - cache['ts']) < self.CACHE_TTL:
                return cache['value']
        value = loader()
        with self._cache_lock:
            cache = getattr(self, '_' + name + '_cache')
            cache['ts'] = time.time()
            cache['value'] = value
        return value

    def _invalidate(self, name: str):
        with self._cache_lock:
            cache = getattr(self, '_' + name + '_cache')
            cache['ts'] = 0.0
            cache['value'] = None

    def create_filter(self, filter_data: Dict) -> str:
        """Create a new filter"""
        filter_id = f"filter:{int(time.time() * 1000)}"
        filter_data['id'] = filter_id
        filter_data['created_at'] = datetime.now(timezone.utc).isoformat()
        filter_data['enabled'] = filter_data.get('enabled', True)
        filter_data['notify_telegram'] = filter_data.get('notify_telegram', False)

        self.client.hset(filter_id, mapping=self._jsonify(filter_data))
        self.client.sadd('filters:all', filter_id)
        self._invalidate('filters')
        return filter_id

    def get_filter(self, filter_id: str) -> Optional[Dict]:
        """Get a single filter"""
        data = self.client.hgetall(filter_id)
        if not data:
            return None

        # Parse JSON fields (including booleans stored as JSON)
        for key in ['conditions', 'enabled', 'notify_telegram']:
            if key in data:
                try:
                    data[key] = json.loads(data[key])
                except:
                    pass
        return data

    def _load_filters(self) -> List[Dict]:
        filter_ids = self.client.smembers('filters:all')
        filters = []
        for filter_id in filter_ids:
            f = self.get_filter(filter_id)
            if f:
                filters.append(f)
        return sorted(filters, key=lambda x: x.get('created_at', ''), reverse=True)

    def get_filters(self) -> List[Dict]:
        """Get all filters (cached briefly)."""
        def loader():
            return self._load_filters()
        try:
            return self._cached('filters', loader)
        except redis.exceptions.ConnectionError:
            raise
        except Exception:
            # On transient errors serve the last known list if we have one
            with self._cache_lock:
                if self._filters_cache['value'] is not None:
                    return self._filters_cache['value']
            raise

    def update_filter(self, filter_id: str, filter_data: Dict) -> bool:
        """Update a filter"""
        if not self.client.exists(filter_id):
            return False

        filter_data['updated_at'] = datetime.now(timezone.utc).isoformat()
        self.client.hset(filter_id, mapping=self._jsonify(filter_data))
        self._invalidate('filters')
        return True

    def delete_filter(self, filter_id: str) -> bool:
        """Delete a filter"""
        if not self.client.exists(filter_id):
            return False

        self.client.delete(filter_id)
        self.client.srem('filters:all', filter_id)
        self._invalidate('filters')
        return True

    def get_enabled_filters(self) -> List[Dict]:
        """Get all enabled filters (cached briefly - the receive path calls
        this for every incoming message)."""
        return [f for f in self.get_filters() if f.get('enabled')]

    # ==================== ALERT OPERATIONS ====================

    def store_alert(self, alert_data: Dict) -> str:
        """Store an alert"""
        alert_id = f"alert:{int(time.time() * 1000)}"
        alert_data['id'] = alert_id
        alert_data['timestamp'] = datetime.now(timezone.utc).isoformat()
        alert_data['acknowledged'] = False

        self.client.hset(alert_id, mapping=self._jsonify(alert_data))
        self.client.zadd('alerts:timeline', {alert_id: time.time()})
        self._invalidate('stats')

        # Keep alerts for 30 days
        self.client.expire(alert_id, 30 * 24 * 3600)

        return alert_id

    def get_alerts(self, limit: int = 50, acknowledged: bool = None) -> List[Dict]:
        """Get alerts"""
        alert_ids = self.client.zrevrange('alerts:timeline', 0, limit * 2)
        alerts = []
        for alert_id in alert_ids:
            data = self.client.hgetall(alert_id)
            if data:
                for key in ['acknowledged']:
                    if key in data:
                        try:
                            data[key] = json.loads(data[key])
                        except:
                            pass

                if acknowledged is None or data.get('acknowledged') == acknowledged:
                    alerts.append(data)
                    if len(alerts) >= limit:
                        break
        return alerts

    def _unacknowledged_alert_count(self, cap: int = 200) -> int:
        """Count unacknowledged alerts without fetching whole hashes: pipeline
        a single HGET('acknowledged') per candidate id."""
        ids = self.client.zrevrange('alerts:timeline', 0, cap - 1)
        if not ids:
            return 0
        pipe = self.client.pipeline()
        for aid in ids:
            pipe.hget(aid, 'acknowledged')
        vals = pipe.execute()
        return sum(1 for v in vals if str(v or 'false').strip().lower() != 'true')

    def acknowledge_alert(self, alert_id: str) -> bool:
        """Acknowledge an alert"""
        if not self.client.exists(alert_id):
            return False
        self.client.hset(alert_id, 'acknowledged', 'true')
        self._invalidate('stats')
        return True

    def acknowledge_all_alerts(self) -> int:
        """Acknowledge all unacknowledged alerts"""
        alert_ids = self.client.zrevrange('alerts:timeline', 0, -1)
        count = 0
        pipe = self.client.pipeline()
        for alert_id in alert_ids:
            pipe.hget(alert_id, 'acknowledged')
        vals = pipe.execute()
        pipe = self.client.pipeline()
        for alert_id, v in zip(alert_ids, vals):
            acked = str(v or 'false').strip().lower() == 'true'
            if not acked:
                pipe.hset(alert_id, 'acknowledged', 'true')
                count += 1
        if count:
            pipe.execute()
        if count:
            self._invalidate('stats')
        return count

    def clear_acknowledged_alerts(self) -> int:
        """Delete all acknowledged alerts"""
        alert_ids = self.client.zrevrange('alerts:timeline', 0, -1)
        count = 0
        pipe = self.client.pipeline()
        for alert_id in alert_ids:
            pipe.hget(alert_id, 'acknowledged')
        vals = pipe.execute()
        pipe = self.client.pipeline()
        for alert_id, v in zip(alert_ids, vals):
            acked = str(v or 'false').strip().lower() == 'true'
            if acked:
                pipe.delete(alert_id)
                pipe.zrem('alerts:timeline', alert_id)
                count += 1
        if count:
            pipe.execute()
        if count:
            self._invalidate('stats')
        return count

    # ==================== SETTINGS OPERATIONS ====================

    def get_settings(self) -> Dict:
        """Get application settings (cached briefly - called per log message
        and per Docker log line).

        Keys missing from the stored hash are filled from the defaults so old
        settings (saved before a new key existed) still resolve cleanly."""
        def loader():
            data = self.client.hgetall('settings')
            if not data:
                return self.get_default_settings()
            for key in data:
                try:
                    data[key] = json.loads(data[key])
                except:
                    pass
            # Merge defaults for keys this hash predates
            defaults = self.get_default_settings()
            for k, v in defaults.items():
                data.setdefault(k, v)
            return data
        try:
            return dict(self._cached('settings', loader))
        except redis.exceptions.ConnectionError:
            raise
        except Exception:
            with self._cache_lock:
                if self._settings_cache['value'] is not None:
                    return dict(self._settings_cache['value'])
            raise

    def save_settings(self, settings: Dict):
        """Save application settings (MERGE semantics).

        The stored hash is updated key-by-key, so a partial client payload
        (e.g. the Docker page posting only exclusions) can never wipe the
        Telegram / AI / retention configuration that was not part of it.
        """
        try:
            existing_raw = self.client.hgetall('settings')
        except Exception:
            existing_raw = {}
        merged = {}
        for k, v in existing_raw.items():
            try:
                merged[k] = json.loads(v)
            except Exception:
                merged[k] = v
        merged.update({k: v for k, v in settings.items()})
        self.client.hset('settings',
                         mapping={k: json.dumps(v) for k, v in merged.items()})
        self._invalidate('settings')
        self._invalidate('stats')

    def get_default_settings(self) -> Dict:
        """Get default settings"""
        return {
            'telegram_enabled': False,
            'telegram_bot_token': '',
            'telegram_chat_id': '',
            'telegram_cooldown_minutes': 60,
            'ollama_enabled': True,
            'ollama_host': Config.OLLAMA_HOST,
            'ollama_model': Config.OLLAMA_MODEL,
            # Which protocol the analyzer speaks - 'openai' (OpenAI-compatible
            # /v1 endpoints: vLLM/SGLang/Ollama /v1...) or 'ollama' (native
            # Ollama API).  User-selectable in Settings; falls back to the
            # env AI_PROVIDER default.
            'ai_provider': getattr(Config, 'AI_PROVIDER', 'openai'),
            'analysis_interval': Config.ANALYSIS_INTERVAL_MINUTES,
            'log_retention_hours': Config.LOG_RETENTION_HOURS,
            'max_logs_per_analysis': Config.MAX_LOGS_PER_ANALYSIS,
            'batch_sample_limit': int(os.environ.get('BATCH_SAMPLE_LIMIT', 200)),
            'auto_analyze': True,
            'alert_on_critical': True,
            'alert_on_error': True,
            'docker_enabled': True,
            'docker_excluded_containers': [],
            'hide_duplicates_default': False,
            # Health watchdog (editable from General Settings; env only seeds)
            'health_watch_minutes': int(os.environ.get('HEALTH_WATCH_MINUTES', 5)),
            'health_backlog_warn': int(os.environ.get('HEALTH_BACKLOG_WARN', 2000)),
            'health_alert_cooldown_min': int(os.environ.get('HEALTH_ALERT_COOLDOWN_MIN', 30)),
            'health_daily_summary': os.environ.get('HEALTH_DAILY_SUMMARY', '1').lower() in ('1', 'true', 'yes')
        }

    # ==================== NOTIFICATION COOLDOWN OPERATIONS ====================

    def check_notification_cooldown(self, hostname: str, filter_id: str) -> bool:
        """Return True if a notification for this host+filter was recently sent (still in cooldown)."""
        key = f"notif_cooldown:{hostname}:{filter_id}"
        return self.client.exists(key) > 0

    def set_notification_cooldown(self, hostname: str, filter_id: str, cooldown_minutes: int):
        """Record that a notification was just sent and set its TTL."""
        if cooldown_minutes <= 0:
            return
        key = f"notif_cooldown:{hostname}:{filter_id}"
        self.client.set(key, '1', ex=cooldown_minutes * 60)

    # ==================== AI HISTORY OPERATIONS ====================

    # History entries used to live for a hard-coded 30 days while the log
    # retention in Settings could be far shorter (e.g. 240h) - old analysis
    # records outlived the logs they referenced.  The TTL now follows the same
    # Log Retention (hours) setting as the log hashes (floor of 1h so a "0"
    # value cannot make records immortal by accident).
    def _history_ttl_seconds(self) -> int:
        try:
            seconds = self._retention_seconds()
        except Exception:
            seconds = 0
        if seconds <= 0:
            seconds = 30 * 24 * 3600  # historical default
        return max(seconds, 3600)

    def store_analysis_history(self, analysis_data: Dict) -> str:
        """Store an AI analysis history entry"""
        history_id = f"ai_history:{int(time.time() * 1000)}"
        analysis_data['id'] = history_id
        analysis_data['timestamp'] = datetime.now(timezone.utc).isoformat()

        self.client.hset(history_id, mapping=self._jsonify(analysis_data))
        self.client.zadd('ai_history:timeline', {history_id: time.time()})

        # Keep history for as long as the configured log retention
        self.client.expire(history_id, self._history_ttl_seconds())

        return history_id

    def update_analysis_history(self, history_id: str, analysis_data: Dict) -> bool:
        """Overwrite fields (analysis, logs_analyzed, ...) on an existing history
        entry. Returns False if the entry does not exist."""
        if not self.client.exists(history_id):
            return False
        mapping = {}
        for k, v in analysis_data.items():
            if isinstance(v, (dict, list, bool)):
                mapping[k] = json.dumps(v)
            else:
                mapping[k] = str(v)
        if mapping:
            self.client.hset(history_id, mapping=mapping)
        return True

    def _prune_ai_history_dead(self):
        """Drop timeline members whose hash already expired (TTL).  Expired
        keys would otherwise stay in the ai_history:timeline zset forever and
        inflate the pagination total.  Throttled to at most one pass/minute."""
        now = time.time()
        if now - getattr(self, '_ai_history_pruned_at', 0.0) < 60:
            return
        self._ai_history_pruned_at = now
        try:
            ids = self.client.zrange('ai_history:timeline', 0, -1)
            CHUNK = 2000
            removed = 0
            for i in range(0, len(ids), CHUNK):
                chunk = ids[i:i + CHUNK]
                pipe = self.client.pipeline()
                for hid in chunk:
                    pipe.exists(hid)
                results = pipe.execute()
                dead = [hid for hid, ok in zip(chunk, results) if not ok]
                if dead:
                    self.client.zrem('ai_history:timeline', *dead)
                    removed += len(dead)
            if removed:
                print(f"[Redis] Pruned {removed} expired AI-history records")
        except Exception as e:
            print(f"[Redis] ai_history prune failed: {e}")

    def get_analysis_history(self, limit: int = 50, offset: int = 0) -> List[Dict]:
        """Get AI analysis history (newest-first page).

        ``offset`` enables pagination; dead (TTL-expired) members are pruned
        first so page boundaries stay aligned with the live records.
        """
        self._prune_ai_history_dead()
        start = max(0, offset)
        stop = offset + max(0, limit) - 1
        history_ids = self.client.zrevrange('ai_history:timeline', start, stop)
        history = []
        for history_id in history_ids:
            data = self.client.hgetall(history_id)
            if data:
                for key in ['analysis', 'logs_analyzed']:
                    if key in data:
                        try:
                            data[key] = json.loads(data[key])
                        except:
                            pass
                history.append(data)
        return history

    def get_analysis_history_count(self) -> int:
        """Return the total number of LIVE AI analysis history entries (dead
        TTL-expired members are pruned first, so this matches what pagination
        walks over)."""
        self._prune_ai_history_dead()
        return self.client.zcard('ai_history:timeline')

    def get_ai_history_stats(self) -> Dict:
        """Aggregate status counts across ALL live analysis-history entries.

        Called from the AI History page (which is paginated now, so the
        per-page payload alone can no longer feed the summary cards).
        Cached ~60s in-process: only recomputed on page open / refresh.
        """
        with self._cache_lock:
            now = time.time()
            cached = getattr(self, '_ai_stats_cache', None)
            if cached and (now - cached[0]) < 60:
                return cached[1]

        self._prune_ai_history_dead()
        ids = self.client.zrange('ai_history:timeline', 0, -1)
        total = len(ids)
        counts = {'healthy': 0, 'warning': 0, 'critical': 0, 'other': 0}
        if ids:
            pipe = self.client.pipeline()
            for hid in ids:
                pipe.hmget(hid, 'type', 'analysis')
            rows = pipe.execute()
            for htype, analysis_raw in rows:
                status = None
                if analysis_raw:
                    try:
                        analysis = json.loads(analysis_raw)
                        if isinstance(analysis, dict):
                            if htype == 'single':
                                if analysis.get('is_critical') is True:
                                    status = 'critical'
                                else:
                                    status = analysis.get('category')
                            else:
                                status = analysis.get('overall_status') or analysis.get('category')
                    except Exception:
                        status = None
                s = str(status or '').lower()
                if s in ('healthy', 'info'):
                    counts['healthy'] += 1
                elif s == 'warning':
                    counts['warning'] += 1
                elif s in ('critical', 'error'):
                    counts['critical'] += 1
                else:
                    counts['other'] += 1

        result = {'total': total, **counts}
        self._ai_stats_cache = (time.time(), result)
        return result

    def purge_stale_failed_history(self, limit: int = 150) -> int:
        """Delete FAILED analysis-history records (parse error / no response)
        whose referenced logs have since been successfully analyzed.

        Failed batches stay in history for manual re-analysis until they
        succeed; when a later retry DOES succeed the record should disappear.
        The newest-record in-place update covers the common case, but older
        failed records for the same logs (from earlier retry rounds) would
        linger - this sweep removes them so the AI History page only shows
        genuinely still-failing batches."""
        ids = self.client.zrevrange('ai_history:timeline', 0, max(0, limit - 1))
        removed = 0
        for hid in ids:
            try:
                raw = self.client.hget(hid, 'analysis') or ''
                if 'Unable to parse' not in raw and 'did not respond' not in raw:
                    continue
                log_ids_raw = self.client.hget(hid, 'log_ids')
                try:
                    log_ids = json.loads(log_ids_raw) if log_ids_raw else []
                except Exception:
                    log_ids = []
                if not isinstance(log_ids, list) or not log_ids:
                    continue
                # Are all still-existing referenced logs analyzed?
                checked = []
                for lid in log_ids[:100]:
                    v = self.client.hget(lid, 'analyzed')
                    if v is None:
                        continue  # referenced log already gone
                    checked.append(str(v).strip().lower() == 'true')
                if checked and all(checked):
                    self.client.delete(hid)
                    self.client.zrem('ai_history:timeline', hid)
                    removed += 1
            except Exception:
                continue
        if removed:
            print(f"[Redis] Purged {removed} stale failed AI-history record(s)")
        return removed

    def get_analysis_history_entry(self, history_id: str) -> Optional[Dict]:
        """Get a single AI analysis history entry"""
        data = self.client.hgetall(history_id)
        if not data:
            return None
        for key in ['analysis', 'logs_analyzed', 'log_ids']:
            if key in data:
                try:
                    data[key] = json.loads(data[key])
                except:
                    pass
        return data

    def get_latest_auto_history(self) -> Optional[Dict]:
        """Return the newest automatic-analysis history entry (or None). Used to
        de-duplicate consecutive failed runs of the same backlog batch."""
        ids = self.client.zrevrange('ai_history:timeline', 0, 0)
        if not ids:
            return None
        return self.get_analysis_history_entry(ids[0])

    def delete_analysis_history(self, history_id: str) -> bool:
        """Delete an AI analysis history entry"""
        if not self.client.exists(history_id):
            return False
        self.client.delete(history_id)
        self.client.zrem('ai_history:timeline', history_id)
        return True

    def clear_all_analysis_history(self) -> int:
        """Clear all AI analysis history entries"""
        history_ids = self.client.zrange('ai_history:timeline', 0, -1)
        count = 0
        for history_id in history_ids:
            self.client.delete(history_id)
            count += 1
        self.client.delete('ai_history:timeline')
        return count

    # ==================== STATS OPERATIONS ====================

    def get_stats(self) -> Dict:
        """Get system statistics (served from a ~2s in-process cache because
        /api/stats is polled by every open dashboard about once per second).

        The unacknowledged-alert count used to scan up to `limit*2` whole
        hashes sequentially and source/severity lists used blocking KEYS scans;
        both are now cheap (pipelined HGETs + SCAN).
        """
        with self._cache_lock:
            now = time.time()
            if self._stats_cache['value'] is not None and (now - self._stats_cache['ts']) < self.STATS_CACHE_TTL:
                return self._stats_cache['value']

        now = time.time()
        hour_ago = now - 3600
        day_ago = now - 86400

        stats = {
            'total_logs': self.get_logs_count(),
            'logs_last_hour': self.client.zcount('logs:timeline', hour_ago, now),
            'logs_last_day': self.client.zcount('logs:timeline', day_ago, now),
            'total_filters': self.client.scard('filters:all'),
            'total_alerts': self.client.zcard('alerts:timeline'),
            'unacknowledged_alerts': self._unacknowledged_alert_count(),
            'sources': self.get_sources(),
            'severities': self.get_severities()
        }
        with self._cache_lock:
            self._stats_cache['ts'] = time.time()
            self._stats_cache['value'] = stats
        return stats

    # ==================== USER OPERATIONS ====================

    def create_user(self, user_data: Dict) -> str:
        """Create a new user"""
        user_id = f"user:{int(time.time() * 1000)}"
        user_data['id'] = user_id
        user_data['created_at'] = datetime.now(timezone.utc).isoformat()

        self.client.hset(user_id, mapping=self._jsonify(user_data))
        self.client.sadd('users:all', user_id)

        # Index by username for quick lookup
        self.client.set(f"users:username:{user_data['username'].lower()}", user_id)

        return user_id

    def get_user(self, user_id: str) -> Optional[Dict]:
        """Get a user by ID"""
        data = self.client.hgetall(user_id)
        if not data:
            return None
        return data

    def get_user_by_username(self, username: str) -> Optional[Dict]:
        """Get a user by username"""
        user_id = self.client.get(f"users:username:{username.lower()}")
        if not user_id:
            return None
        return self.get_user(user_id)

    def get_all_users(self) -> List[Dict]:
        """Get all users"""
        user_ids = self.client.smembers('users:all')
        users = []
        for user_id in user_ids:
            user = self.get_user(user_id)
            if user:
                users.append(user)
        return sorted(users, key=lambda x: x.get('username', ''))

    def update_user(self, user_id: str, user_data: Dict) -> bool:
        """Update a user"""
        if not self.client.exists(user_id):
            return False
        self.client.hset(user_id, mapping=self._jsonify(user_data))
        return True

    def delete_user(self, user_id: str) -> bool:
        """Delete a user"""
        user = self.get_user(user_id)
        if not user:
            return False

        # Remove username index
        if user.get('username'):
            self.client.delete(f"users:username:{user['username'].lower()}")

        self.client.delete(user_id)
        self.client.srem('users:all', user_id)
        return True

    def ensure_admin_exists(self):
        """Ensure at least one admin user exists"""
        from werkzeug.security import generate_password_hash

        users = self.get_all_users()
        admin_exists = any(u.get('role') == 'admin' for u in users)

        if not admin_exists:
            # Create default admin user
            admin_data = {
                'username': 'admin',
                'password_hash': generate_password_hash('admin'),
                'email': '',
                'role': 'admin'
            }
            self.create_user(admin_data)
            print("[Redis] Created default admin user (username: admin, password: admin)")
            return True
        return False


# Global instance
redis_client = RedisClient()
