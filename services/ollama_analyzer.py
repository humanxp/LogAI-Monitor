# Copyright (C) 2026 Fotios Tsiadimos
# SPDX-License-Identifier: GPL-3.0-or-later

import requests
import os
import threading

# 'ollama' is only required when AI_PROVIDER='ollama'; keep the import defensive
# so the module still loads in environments (or openai-only setups) without it.
try:
    import ollama
except ImportError:  # pragma: no cover
    ollama = None
import time
from datetime import datetime
from typing import Dict, List, Optional, Callable
from config import Config


def _ensure_host_prefix(items, hosts):
    out = []
    if not hosts:
        out.extend(items or [])
        return out
    for it in (items or []):
        it = _coerce_text(it).strip()
        if not it:
            continue
        if it.startswith('['):
            closing = it.find(']')
            if closing != -1:
                inner = it[1:closing].strip()
                # Accept only prefixes that look like a real host token. Small
                # models sometimes wrap log content in brackets (e.g.
                # "[Hostd:: sub=Default] ...") - strip that wrapper instead of
                # keeping a bogus "host" label.
                plausible = bool(inner) and not any(c in inner for c in ' :=') and '[' not in inner
                if plausible:
                    out.append(it)
                    continue
                it = it[closing + 1:].strip()
                if not it:
                    continue
        matched = next((h for h in hosts if h and h in it), hosts[0])
        out.append('[' + matched + '] ' + it)
    return out


def _coerce_text(item):
    """Safely turn an extracted JSON value into a displayable string.

    Small models (e.g. Qwen3-0.6B) sometimes emit ``issues_found`` /
    ``recommendations`` entries as OBJECTS instead of strings; previously a
    ``.strip()`` on such an item crashed the whole batch analysis
    ("'dict' object has no attribute 'strip'"). Prefer common text-ish keys,
    otherwise fall back to a str() of the object - never raise."""
    if item is None:
        return ''
    if isinstance(item, str):
        return item
    if isinstance(item, dict):
        for k in ('text', 'message', 'description', 'summary',
                  'issue', 'problem', 'recommendation', 'action', 'detail'):
            v = item.get(k)
            if isinstance(v, str) and v.strip():
                return v
    try:
        return str(item)
    except Exception:
        return ''


def _prompt_safe(value, max_len=200):
    """Neutralise characters that would break JSON when a model echoes log
    content back verbatim.  Double quotes, backslashes and control characters
    (newlines / tabs) are replaced so the model can never produce unescaped
    copies of them inside JSON string values - a copied raw line containing
    e.g. ``disk "sda1" is full`` used to make the whole reply unparseable."""
    if value is None:
        return ''
    s = str(value)
    s = s.replace('\\', '/').replace('"', "'")
    s = s.replace('\r', ' ').replace('\n', ' ').replace('\t', ' ')
    return s.strip()[:max_len]


def _coerce_str_list(value):
    """Normalize an extracted list field to a list of plain strings."""
    if not isinstance(value, list):
        return []
    out = []
    for item in value:
        text = _coerce_text(item).strip()
        if text:
            out.append(text)
    return out

def _strip_hostname(v):
    if not v:
        return ''
    v = str(v).strip()
    if v.startswith('[') and ']' in v:
        return v[1:v.index(']')].strip()
    return v



# Batch analysis sampling: how many log lines to send to the model, and the
# order to prefer them by severity so real issues are not hidden behind a cap.
BATCH_SAMPLE_LIMIT = int(os.environ.get('BATCH_SAMPLE_LIMIT', 200))
_SEV_RANK = {
    'critical': 0, 'emerg': 0, 'alert': 0,
    'error': 1, 'err': 1,
    'warning': 2, 'warn': 2,
    'notice': 3,
    'info': 4,
    'debug': 5,
}


def _severity_sorted_logs(logs, limit):
    """Return up to ``limit`` logs, ordered so higher-severity (critical/error/
    warning) lines come first, then the rest. Important logs are placed first."""
    if not logs:
        return []
    high = []
    low = []
    for l in logs:
        sev = str(l.get('severity', 'info')).lower()
        if _SEV_RANK.get(sev, 4) <= 2:
            high.append(l)
        else:
            low.append(l)
    ordered = high + low
    return ordered[:limit]


def _normalize_base_url(url) -> str:
    """Make an OpenAI-compatible endpoint usable as the analyzer base URL.

    vLLM / SGLang / omlx serve under ``/v1`` (``/v1/chat/completions``,
    ``/v1/models``).  The UI lets the operator type either
    ``http://host:8000/v1`` or ``http://host:8000`` - normalize both to the
    ``/v1`` form so a missing suffix no longer produces
    ``404 /chat/completions``.
    """
    s = (url or '').strip().rstrip('/')
    if not s:
        return ''
    return s if s.endswith('/v1') else s + '/v1'


class OllamaAnalyzer:
    """AI-powered log analyzer.

    Supports two AI providers (selected via ``Config.AI_PROVIDER``):

    * ``ollama``  - native Ollama API (``/api/generate``, ``/api/tags``)
    * ``openai``  - any OpenAI-compatible endpoint (``/chat/completions``,
      ``/models``), e.g. vLLM / SGLang / omlx servers.

    The public method signatures are unchanged so callers (app.py) work with
    either provider transparently.
    """

    def __init__(self, alert_callback: Callable = None, cache_ttl: int = 30):
        self.host = Config.OLLAMA_HOST
        self.model = Config.OLLAMA_MODEL
        self.provider = getattr(Config, 'AI_PROVIDER', 'ollama')
        self.base_url = _normalize_base_url(getattr(Config, 'AI_BASE_URL', ''))
        self.api_key = getattr(Config, 'AI_API_KEY', '')
        self.alert_callback = alert_callback
        self.running = False
        self.analysis_thread = None
        # Lines sent to the model per batch (severity-priority sampling cap).
        # Defaults to the env value; Settings -> General Settings overrides it
        # at runtime (app.py syncs it through _sync_analyzer_settings).
        self.batch_sample_limit = int(os.environ.get('BATCH_SAMPLE_LIMIT', 200))
        # Read timeout for model calls.  Fast GPU endpoints answer in seconds,
        # but a CPU-bound 3B Ollama box can take 2-5 minutes per batch - a too
        # small timeout turns "slow but working" into repeated read-timeout
        # failures.  Default 180s, raise with AI_READ_TIMEOUT for slow hosts.
        self._read_timeout = int(os.environ.get('AI_READ_TIMEOUT', 180))

        # Caching for availability and models to reduce frequent list calls
        self._cache_ttl = cache_ttl  # seconds
        self._last_check_time = 0.0
        self._last_available = False
        self._last_models: List[str] = []
        # Lock to prevent concurrent refreshes (de-duplicate simultaneous checks)
        self._check_lock = threading.Lock()

    def apply_endpoint(self, host: str = None, model: str = None, provider: str = None):
        """Point the analyzer at the endpoint/model/provider chosen in Settings.

        ``provider`` ('openai' / 'ollama') is kept runtime-tunable so an
        operator can switch between an OpenAI-compatible backend (vLLM /
        SGLang / Ollama /v1...) and a native-Ollama backend from the UI - the
        two speak different APIs and return model lists in different formats.
        For 'openai' this also rewrites ``base_url`` (the value that actually
        issues ``/chat/completions`` and ``/models``).  Previously a Settings
        "AI Endpoint" change only updated ``self.host`` (used by the
        native-Ollama client), so address changes silently never took effect.
        """
        if provider and str(provider).strip().lower() in ('openai', 'ollama'):
            self.provider = str(provider).strip().lower()
        if model:
            self.model = model
        if host is None:
            return
        host = str(host).strip()
        if not host:
            return
        self.host = host
        if self.provider == 'openai':
            self.base_url = _normalize_base_url(host)

    # ------------------------------------------------------------------
    # Provider dispatch helpers
    # ------------------------------------------------------------------
    def _complete(self, prompt: str, temperature: float = 0.3, max_tokens: int = 256,
                  frequency_penalty: float = 0.0) -> str:
        """Run a single prompt through the configured provider and return the text.

        ``frequency_penalty`` is passed through for OpenAI-compatible backends
        that support it (vLLM does); it suppresses the degenerate repetition
        loops small models occasionally enter when forced into strict JSON
        output (those used to burn the whole token budget and truncate).

        When the endpoint reports finish_reason='length' the reply was cut
        mid-generation; we retry ONCE with a doubled budget (capped at 4k so a
        runaway loop fails fast instead of being fed ever more tokens).
        """
        if self.provider == 'openai':
            url = self.base_url.rstrip('/') + '/chat/completions'
            headers = {'Content-Type': 'application/json'}
            if self.api_key:
                headers['Authorization'] = f'Bearer {self.api_key}'

            def _call(tokens: int) -> tuple:
                payload = {
                    'model': self.model,
                    'messages': [{'role': 'user', 'content': prompt}],
                    'temperature': temperature,
                    'max_tokens': tokens,
                }
                if frequency_penalty > 0:
                    payload['frequency_penalty'] = frequency_penalty
                resp = requests.post(
                    url,
                    headers=headers,
                    json=payload,
                    # Separate connect/read timeouts: an unreachable AI server must
                    # fail fast (8s connect) instead of holding the analysis lock
                    # for the full read timeout and blocking every following batch.
                    # The read timeout is configurable (AI_READ_TIMEOUT) because
                    # slow CPU-backed models legitimately take minutes per batch.
                    timeout=(8, self._read_timeout),
                )
                if not resp.ok:
                    # Keep the server's own reason (e.g. "exceeds the available
                    # context size") instead of a bare "400 Client Error".
                    body = (resp.text or '')[:220].replace('\n', ' ')
                    raise RuntimeError(
                        f'AI endpoint HTTP {resp.status_code}: {body}')
                data = resp.json()
                choice = (data.get('choices') or [{}])[0]
                return (choice.get('message', {}).get('content') or '').strip(), \
                       choice.get('finish_reason')

            content, finish = _call(max_tokens)
            if finish == 'length' and max_tokens < 16384:
                print(f"[OllamaAnalyzer] Output truncated at {max_tokens} tokens "
                      f"(finish_reason=length); retrying with a larger budget")
                content, finish = _call(min(max_tokens * 2, 4096))
            if finish == 'length':
                raise RuntimeError(
                    f'AI response truncated at {max_tokens} tokens (finish_reason=length); '
                    f'consider a larger model or shorter prompts')
            return content

        # Default: native Ollama
        if ollama is None:
            raise RuntimeError("ollama package is not installed; cannot use Ollama provider")
        client = ollama.Client(host=self.host)
        response = client.generate(
            model=self.model,
            prompt=prompt,
            options={'temperature': temperature, 'num_predict': max_tokens},
        )
        return response['response'].strip()

    def _list_models(self) -> List[str]:
        """Return the list of available model names for the configured provider."""
        if self.provider == 'openai':
            url = self.base_url.rstrip('/') + '/models'
            headers = {}
            if self.api_key:
                headers['Authorization'] = f'Bearer {self.api_key}'
            # Short timeout: when the AI backend is down/slow the UI should get
            # a clear "AI not available" quickly instead of hanging ~20s.
            resp = requests.get(url, headers=headers, timeout=8)
            resp.raise_for_status()
            return [m['id'] for m in resp.json().get('data', [])]

        if ollama is None:
            raise RuntimeError("ollama package is not installed; cannot use Ollama provider")
        client = ollama.Client(host=self.host)
        return [m['name'] for m in client.list().get('models', [])]

    # ------------------------------------------------------------------
    # Availability / model listing (cached)
    # ------------------------------------------------------------------
    def is_available(self, force_refresh: bool = False) -> bool:
        """Check if the AI backend is available. Uses short TTL cache and a lock."""
        now = time.time()
        if not force_refresh and (now - self._last_check_time) < self._cache_ttl:
            return self._last_available

        # Ensure only one thread performs the actual list call at a time
        with self._check_lock:
            # Re-check cache after acquiring lock (another thread may have refreshed)
            now = time.time()
            if not force_refresh and (now - self._last_check_time) < self._cache_ttl:
                return self._last_available

            try:
                models = self._list_models()
                self._last_available = True
                self._last_models = models
                self._last_check_time = time.time()
                return True
            except Exception as e:
                print(f"[OllamaAnalyzer] AI backend not available: {e}")
                self._last_available = False
                self._last_models = []
                self._last_check_time = time.time()
                return False

    def get_models(self, force_refresh: bool = False) -> List[str]:
        """Get available models, using cached result when recent. Delegates refresh to is_available."""
        now = time.time()
        if not force_refresh and (now - self._last_check_time) < self._cache_ttl and self._last_models:
            return self._last_models

        # Use is_available which handles locking and refresh
        self.is_available(force_refresh=force_refresh)
        return self._last_models

    def cached_availability(self) -> bool:
        """Return the last known availability without triggering a refresh."""
        return self._last_available

    def last_check_age(self) -> Optional[float]:
        """Return seconds since last check, or None if never checked."""
        if self._last_check_time == 0.0:
            return None
        return time.time() - self._last_check_time

    # ------------------------------------------------------------------
    # Shared JSON extraction
    # ------------------------------------------------------------------
    # Keys that identify an "analysis-shaped" object (either the single-log or
    # the batch schema). Used to pick the right object when the model wraps the
    # JSON in prose that itself contains balanced braces.
    _ANALYSIS_SCHEMA_KEYS = (
        'overall_status', 'issues_found', 'critical_count', 'recommendations',
        'affected_hosts', 'alert_message', 'is_critical', 'category',
        'summary', 'recommendation', 'alert_user',
    )

    @staticmethod
    def _extract_json(response_text: str) -> Optional[Dict]:
        """Best-effort extraction of a JSON object from a model response.

        Strategy (each step is a superset of the previous):
        1. Direct parse of the whole (trimmed) text.
        2. Every ```json / ``` fenced block in the text.
        3. A string-aware scan for top-level balanced ``{...}`` objects —
           braces inside JSON string values are ignored, so log content that
           contains a lone ``}`` can no longer break the balance.  All
           candidates are scored by how many analysis-schema keys they carry;
           the best (ties -> the one appearing last) wins.  This tolerates
           commentary before/after the JSON, truncated candidates and
           prose-wrapped replies.

        The old implementation (a) matched only the FIRST fenced block with a
        non-greedy regex that stopped at the first ``}``, and (b) counted
        braces without knowing about strings - a message value holding a bare
        ``}`` made the final object look unbalanced and the whole batch was
        reported as "Unable to parse AI response".
        """
        import json
        import re

        text = (response_text or '').strip()
        if not text:
            return None

        # 1) Direct parse of the whole response
        try:
            parsed = json.loads(text)
            if isinstance(parsed, dict):
                return parsed
        except json.JSONDecodeError:
            pass

        # 2) Every markdown fenced block (```json / ```)
        for m in re.finditer(r'```[^\n]*\n(.*?)```', text, re.DOTALL):
            content = m.group(1).strip()
            try:
                parsed = json.loads(content)
                if isinstance(parsed, dict):
                    return parsed
            except json.JSONDecodeError:
                continue

        # 3) String-aware scan of all top-level balanced objects
        def _top_level_objects(s: str):
            n = len(s)
            i = 0
            while i < n:
                if s[i] != '{':
                    i += 1
                    continue
                start = i
                depth = 0
                in_str = False
                escaped = False
                j = i
                while j < n:
                    c = s[j]
                    if in_str:
                        if escaped:
                            escaped = False
                        elif c == '\\':
                            escaped = True
                        elif c == '"':
                            in_str = False
                    elif c == '"':
                        in_str = True
                    elif c == '{':
                        depth += 1
                    elif c == '}':
                        depth -= 1
                        if depth == 0:
                            yield s[start:j + 1]
                            break
                    j += 1
                i = max(j + 1, i + 1)

        best = None
        best_score = -1
        best_idx = -1
        for idx, candidate in enumerate(_top_level_objects(text)):
            try:
                parsed = json.loads(candidate)
            except json.JSONDecodeError:
                continue
            if not isinstance(parsed, dict):
                continue
            score = sum(1 for k in OllamaAnalyzer._ANALYSIS_SCHEMA_KEYS if k in parsed)
            if score > best_score or (score == best_score and idx > best_idx):
                best = parsed
                best_score = score
                best_idx = idx

        # Accept only candidates that actually look like an analysis object
        # (carry at least one schema key).  A reply that is prose, an array of
        # unrelated objects, or an object with none of the required keys is NOT
        # a successful analysis - it must surface as a failure so the batch is
        # retried automatically instead of being consumed as an empty result.
        if best is not None and best_score >= 1:
            return best
        return None

    # ------------------------------------------------------------------
    # Analysis operations
    # ------------------------------------------------------------------
    def analyze_log(self, log_entry: Dict) -> Dict:
        """Analyze a single log entry"""
        try:
            prompt = f"""You are a syslog security/health analyzer. Assess this single log entry.

Hostname/IP: {_prompt_safe(log_entry.get('hostname', log_entry.get('source', 'unknown')), 120)}
Source: {_prompt_safe(log_entry.get('source', 'unknown'), 120)}
Severity: {_prompt_safe(log_entry.get('severity', 'unknown'), 32)}
Program: {_prompt_safe(log_entry.get('program', 'unknown'), 60)}
Message: {_prompt_safe(log_entry.get('message', ''), 1200)}

Reply with ONLY a JSON object having exactly these keys:
- "is_critical": boolean (true if it needs immediate attention)
- "category": one of "security", "performance", "application", "system", "network", "other"
- "summary": short one-line summary based only on this log
- "recommendation": short action to take based only on this log, or ""
- "alert_user": boolean (true if the user should be notified)

No markdown, no extra text, only JSON."""

            response_text = self._complete(prompt, temperature=0.1, max_tokens=1024, frequency_penalty=0.3)
            analysis = self._extract_json(response_text)

            # One CORRECTIVE retry: small models occasionally emit malformed
            # JSON (adjacent "host" "text" strings, truncation...). Telling the
            # model the reply was invalid usually makes it fix itself, so this
            # rescues most parse failures before they reach the failure path.
            if analysis is None:
                fix_prompt = (prompt + '\n\nYour previous reply was NOT a single valid JSON object '
                              '(e.g. missing separators / truncated). Reply NOW with ONE complete, valid '
                              'JSON object using exactly the keys above. Every string must be proper JSON.')
                try:
                    response_text = self._complete(fix_prompt, temperature=0.1, max_tokens=1024, frequency_penalty=0.3)
                    analysis = self._extract_json(response_text)
                except Exception:
                    analysis = None

            # No usable JSON object at all: treat it as a FAILURE (not a fake
            # "successful" analysis). The caller records the failure and leaves
            # the log unanalyzed so the next scheduled run retries it - a
            # transient model hiccup then heals itself instead of permanently
            # consuming the log into a "Unable to parse" history entry.  The
            # truncated head of the reply is kept for diagnosis.
            if analysis is None:
                head = (response_text or '').strip()[:300].replace('\n', ' ')
                print(f"[OllamaAnalyzer] Single analysis JSON parse failed; model={self.model} "
                      f"len={len(response_text or '')} reply_head={head!r}")
                return {
                    'success': False,
                    'error': f'Unable to parse AI response (model {self.model}): {head or "empty reply"}'
                }

            analysis.setdefault('overall_status', 'unknown')
            analysis.setdefault('is_critical', False)
            # Normalize list fields (small models sometimes emit objects instead
            # of strings - that must never crash the analysis).
            analysis['issues_found'] = _coerce_str_list(analysis.get('issues_found'))
            analysis['recommendations'] = _coerce_str_list(analysis.get('recommendations'))
            analysis['affected_hosts'] = _coerce_str_list(analysis.get('affected_hosts'))
            host = log_entry.get('hostname') or log_entry.get('source') or 'unknown'
            summary = _coerce_text(analysis.get('summary')).strip()
            rec = _coerce_text(analysis.get('recommendation')).strip()
            if summary and not (summary.startswith('[') and ']' in summary):
                summary = '[' + host + '] ' + summary
            if rec and not rec.startswith('['):
                rec = '[' + host + '] ' + rec
            if not analysis.get('issues_found'):
                analysis['issues_found'] = [summary] if summary else []
            else:
                analysis['issues_found'] = _ensure_host_prefix(analysis.get('issues_found'), [host])
            if not analysis.get('recommendations') and rec:
                analysis['recommendations'] = [rec]
            elif analysis.get('recommendations'):
                analysis['recommendations'] = _ensure_host_prefix(analysis.get('recommendations'), [host])
            if summary:
                analysis['summary'] = summary

            return {
                'success': True,
                'analysis': analysis
            }

        except Exception as e:
            print(f"[OllamaAnalyzer] Error analyzing log: {e}")
            return {
                'success': False,
                'error': str(e)
            }

    def analyze_logs_batch(self, logs: List[Dict]) -> Dict:
        """Analyze multiple logs and summarize findings"""
        try:
            # Prepare log summary for analysis (include hostname/IP for each log).
            # Sample by severity priority (important logs first) up to the limit.
            sample = _severity_sorted_logs(logs, self.batch_sample_limit)
            log_summary = "\n".join([
                f"[{_prompt_safe(l.get('severity', 'info'), 16).upper()}] "
                f"[{_prompt_safe(l.get('hostname', l.get('source', 'unknown')), 120)}] "
                f"{_prompt_safe(l.get('program', 'unknown'), 60)}: "
                f"{_prompt_safe(l.get('message', ''), 200)}"
                for l in sample
            ])

            prompt = f"""You are a syslog security/health analyzer. Assess the logs below.

LOGS:
{log_summary}

Reply with ONLY a JSON object having exactly these keys:
- "overall_status": one of "healthy", "warning", "critical"
- "issues_found": array of short "[HOST] description" strings, one per DISTINCT problem (at most 8). Do NOT copy raw log lines; summarize each distinct pattern in one short line (max 150 chars). Empty array if none
- "critical_count": integer, count of DISTINCT critical problems, 0 if none
- "recommendations": array of short "[HOST] action" strings (actions to fix the issues), at most 5. Empty array if none
- "affected_hosts": array of bare hostname/IP strings involved (no brackets), empty array if none
- "alert_message": short admin alert if critical, else ""

Base everything ONLY on the logs given. Keep the JSON compact. The reply MUST be one single complete valid JSON object - no markdown, no text before or after it, no truncation."""

            response_text = self._complete(prompt, temperature=0.1, max_tokens=2048, frequency_penalty=0.3)
            analysis = self._extract_json(response_text)

            # One CORRECTIVE retry: small models occasionally emit malformed
            # JSON (adjacent "host" "text" strings, truncation...). Telling the
            # model the reply was invalid usually makes it fix itself, so this
            # rescues most parse failures before they reach the failure path.
            if analysis is None:
                fix_prompt = (prompt + '\n\nYour previous reply was NOT a single valid JSON object '
                              '(e.g. missing separators / truncated). Reply NOW with ONE complete, valid '
                              'JSON object using exactly the keys above. Every string must be proper JSON.')
                try:
                    response_text = self._complete(fix_prompt, temperature=0.1, max_tokens=2048, frequency_penalty=0.3)
                    analysis = self._extract_json(response_text)
                except Exception:
                    analysis = None

            # No usable JSON object: report a FAILURE so the batch is recorded
            # as failed and its logs stay unanalyzed for the automatic retry
            # (previously this produced a fake "successful" analysis whose
            # 'Unable to parse AI response' marker permanently consumed the
            # batch and required manual re-analysis).  Keep a truncated head of
            # the model reply for diagnosis.
            if analysis is None:
                head = (response_text or '').strip()[:300].replace('\n', ' ')
                print(f"[OllamaAnalyzer] Batch JSON parse failed; model={self.model} "
                      f"len={len(response_text or '')} reply_head={head!r}")
                return {
                    'success': False,
                    'error': f'Unable to parse AI response (model {self.model}): {head or "empty reply"}'
                }

            # Normalize list fields so a model that returns objects instead of
            # strings (common with small models) can never crash downstream
            # code (previously: "'dict' object has no attribute 'strip'").
            analysis['issues_found'] = _coerce_str_list(analysis.get('issues_found'))
            analysis['recommendations'] = _coerce_str_list(analysis.get('recommendations'))
            analysis['affected_hosts'] = _coerce_str_list(analysis.get('affected_hosts'))

            hosts = []
            for l in logs:
                h = l.get('hostname') or l.get('source') or ''
                if h and h not in hosts:
                    hosts.append(h)
            if hosts:
                analysis['issues_found'] = _ensure_host_prefix(analysis.get('issues_found'), hosts)
                analysis['recommendations'] = _ensure_host_prefix(analysis.get('recommendations'), hosts)
                af = analysis.get('affected_hosts') or []
                cleaned = []
                for h in af:
                    name = _strip_hostname(h)
                    if name and name not in cleaned:
                        cleaned.append(name)
                if cleaned:
                    analysis['affected_hosts'] = cleaned

            return {
                'success': True,
                'analysis': analysis,
                'logs_analyzed': len(logs)
            }

        except Exception as e:
            print(f"[OllamaAnalyzer] Error in batch analysis: {e}")
            return {
                'success': False,
                'error': str(e)
            }

    def generate_alert_message(self, log_entry: Dict, analysis: Dict) -> str:
        """Generate a human-readable alert message"""
        try:
            prompt = f"""Generate a concise Telegram alert message for this log event:

Source: {log_entry.get('source', 'unknown')}
Severity: {log_entry.get('severity', 'unknown')}
Message: {log_entry.get('message', '')}
Analysis: {analysis}

The message should be:
- Clear and actionable
- Under 200 characters
- Include emoji for visual indication
- Start with severity indicator

Respond with ONLY the message text, nothing else."""

            return self._complete(prompt, temperature=0.5, max_tokens=256)

        except Exception as e:
            # Fallback message
            severity_emoji = {
                'critical': '🔴',
                'error': '🟠',
                'warning': '🟡',
                'info': '🔵'
            }.get(log_entry.get('severity', 'info'), '⚪')

            hostname = log_entry.get('hostname', log_entry.get('source', 'unknown'))
            return f"{severity_emoji} [{log_entry.get('severity', 'info').upper()}] [{hostname}] {log_entry.get('program', 'unknown')}: {log_entry.get('message', '')[:100]}"

    def chat(self, message: str, context: str = "") -> str:
        """Chat with the AI about logs"""
        try:
            system_prompt = """You are LogAI Monitor, an expert system administrator assistant.

IMPORTANT RULES:
1. NEVER copy-paste raw log entries in your response
2. Always provide SUMMARIZED insights - brief, clear, and actionable
3. Keep responses SHORT (2-4 sentences max for simple questions)
4. Use bullet points for multiple findings
5. Focus on: What happened? Is it critical? What to do?
6. If logs look normal, say so briefly
7. Highlight only genuinely concerning patterns

CONTEXT UNDERSTANDING - Be smart about what the user is asking:
- If user asks about "alerts", "notifications", "unacknowledged" → Focus on the ALERTS section
- If user asks about "errors", "issues", "problems", "logs", "warnings" → Focus on the LOGS section, analyze log patterns
- If user asks general questions like "anything I should know?" → Check BOTH logs and alerts, prioritize real issues
- If user asks about specific services/hosts/programs → Search the logs for those specific items
- DON'T report alerts when user is clearly asking about log analysis
- DON'T report log errors when user is specifically asking about alerts

Response style examples:
- "All systems normal. No errors or warnings detected in recent logs."
- "⚠️ Found 3 failed SSH login attempts from IP 192.168.1.x. Consider blocking this IP."
- "🔴 Critical: Database connection errors detected. Check MySQL service status."
- "📋 You have 2 unacknowledged alerts that need attention."
- "No alerts currently. Your log activity shows normal operation."
"""

            if context:
                system_prompt += f"\n\nAvailable context (use ONLY what's relevant to the question):\n{context}"

            prompt = f"{system_prompt}\n\nUser question: {message}\n\nProvide a brief, relevant answer based on what the user is actually asking about:"
            return self._complete(prompt, temperature=0.5, max_tokens=1024)

        except Exception as e:
            return f"Error communicating with AI: {str(e)}"

    def check_log_against_filters(self, log_entry: Dict, filters: List[Dict]) -> List[Dict]:
        """Check if a log matches any filters and should trigger an alert"""
        matched_filters = []

        for f in filters:
            if not f.get('enabled', True):
                continue

            conditions = f.get('conditions', {})
            matches = True

            # Check each condition
            if conditions.get('severity'):
                if log_entry.get('severity') not in conditions['severity']:
                    matches = False

            if conditions.get('source_contains') and matches:
                if conditions['source_contains'].lower() not in log_entry.get('source', '').lower():
                    matches = False

            if conditions.get('message_contains') and matches:
                if conditions['message_contains'].lower() not in log_entry.get('message', '').lower():
                    matches = False

            if conditions.get('message_regex') and matches:
                import re
                if not re.search(conditions['message_regex'], log_entry.get('message', ''), re.IGNORECASE):
                    matches = False

            if matches:
                matched_filters.append(f)

        return matched_filters
