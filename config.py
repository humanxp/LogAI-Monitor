# Copyright (C) 2026 Fotios Tsiadimos
# SPDX-License-Identifier: GPL-3.0-or-later

import os

class Config:
    """Application configuration"""

    # version is stored in a simple file at the project root (used by the
    # about page and by CI workflows).  If the file is missing we fall back to
    # an environment variable so that containers built without a copy can still
    # expose something.
    def _load_version():
        path = os.path.join(os.path.dirname(__file__), 'VERSION')
        try:
            with open(path) as f:
                return f.read().strip()
        except Exception:
            return os.environ.get('VERSION', '0.0.0')

    VERSION = _load_version()
    
    # Flask
    SECRET_KEY = os.environ.get('SECRET_KEY', 'logaimonitor-secret-key-change-in-production')
    DEBUG = os.environ.get('DEBUG', 'True').lower() == 'true'
    
    # Redis
    REDIS_HOST = os.environ.get('REDIS_HOST', 'localhost')
    REDIS_PORT = int(os.environ.get('REDIS_PORT', 6379))
    REDIS_DB = int(os.environ.get('REDIS_DB', 0))
    REDIS_PASSWORD = os.environ.get('REDIS_PASSWORD', None)
    
    # Syslog receiver
    SYSLOG_HOST = os.environ.get('SYSLOG_HOST', '0.0.0.0')
    SYSLOG_PORT = int(os.environ.get('SYSLOG_PORT', 5514))
    
    # Docker
    DOCKER_SOCKET = os.environ.get('DOCKER_SOCKET', 'unix://var/run/docker.sock')
    
    # Ollama
    OLLAMA_HOST = os.environ.get('OLLAMA_HOST', 'http://localhost:11434')
    OLLAMA_MODEL = os.environ.get('OLLAMA_MODEL', 'llama3.2')
    # How long to cache Ollama availability/models (seconds)
    OLLAMA_CACHE_TTL_SECONDS = int(os.environ.get('OLLAMA_CACHE_TTL_SECONDS', 60))

    # AI provider: 'ollama' (native Ollama API) or 'openai' (any OpenAI-compatible endpoint,
    # e.g. vLLM / SGLang / omlx). Auto-detects to 'openai' when AI_BASE_URL is provided and
    # AI_PROVIDER is not explicitly set.
    _ai_provider = os.environ.get('AI_PROVIDER', '')
    AI_BASE_URL = os.environ.get('AI_BASE_URL', '')   # e.g. http://your-ai-host:8000/v1
    AI_API_KEY = os.environ.get('AI_API_KEY', '')     # e.g. sk-...
    AI_PROVIDER = (_ai_provider or ('openai' if AI_BASE_URL else 'ollama')).lower()
    
    # Telegram
    TELEGRAM_BOT_TOKEN = os.environ.get('TELEGRAM_BOT_TOKEN', '')
    TELEGRAM_CHAT_ID = os.environ.get('TELEGRAM_CHAT_ID', '')
    
    # Log retention (in hours)
    LOG_RETENTION_HOURS = int(os.environ.get('LOG_RETENTION_HOURS', 2))  # 12 hours
    
    # Analysis
    ANALYSIS_INTERVAL_MINUTES = int(os.environ.get('ANALYSIS_INTERVAL_MINUTES', 5))  # 5 minutes
    MAX_LOGS_PER_ANALYSIS = int(os.environ.get('MAX_LOGS_PER_ANALYSIS', 100))
