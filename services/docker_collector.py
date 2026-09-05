# Copyright (C) 2026 Fotios Tsiadimos
# SPDX-License-Identifier: GPL-3.0-or-later

import docker
import threading
import time
from datetime import datetime, timezone
from typing import Callable, Dict, List
from config import Config

class DockerLogCollector:
    """Collector for Docker container logs"""
    
    def __init__(self, callback: Callable = None, settings_provider: Callable = None):
        self.callback = callback
        self.settings_provider = settings_provider  # Function to get current settings
        self.running = False
        self.client = None
        self.watched_containers = {}
        self.threads = []
    
    def is_enabled(self) -> bool:
        """Check if Docker log collection is enabled"""
        if self.settings_provider:
            settings = self.settings_provider()
            return settings.get('docker_enabled', True)
        return True
    
    def is_container_excluded(self, container_name: str) -> bool:
        """Check if a container is excluded from log collection"""
        if self.settings_provider:
            settings = self.settings_provider()
            excluded = settings.get('docker_excluded_containers', [])
            return container_name in excluded
        return False
        
    def connect(self) -> bool:
        """Connect to Docker daemon"""
        try:
            self.client = docker.DockerClient(base_url=Config.DOCKER_SOCKET)
            self.client.ping()
            print("[DockerLogCollector] Connected to Docker daemon")
            return True
        except Exception as e:
            print(f"[DockerLogCollector] Failed to connect: {e}")
            return False
    
    def get_containers(self) -> List[Dict]:
        """Get list of all containers"""
        if not self.client:
            return []
        
        try:
            containers = self.client.containers.list(all=True)
            return [{
                'id': c.id[:12],
                'name': c.name,
                'image': c.image.tags[0] if c.image.tags else c.image.id[:12],
                'status': c.status,
                'created': c.attrs.get('Created', ''),
                'state': c.attrs.get('State', {})
            } for c in containers]
        except Exception as e:
            print(f"[DockerLogCollector] Error listing containers: {e}")
            return []
    
    def parse_log_line(self, line: str, container_name: str, container_id: str) -> Dict:
        """Parse a Docker log line"""
        severity = 'info'
        
        # Try to detect severity from common patterns
        line_lower = line.lower()
        if any(x in line_lower for x in ['error', 'err', 'fatal', 'fail']):
            severity = 'error'
        elif any(x in line_lower for x in ['warn', 'warning']):
            severity = 'warning'
        elif any(x in line_lower for x in ['debug', 'trace']):
            severity = 'debug'
        elif any(x in line_lower for x in ['critical', 'crit']):
            severity = 'critical'
        
        return {
            'source': f'docker:{container_name}',
            'source_type': 'docker',
            'container_id': container_id,
            'container_name': container_name,
            'timestamp': datetime.now(timezone.utc).isoformat(),
            'severity': severity,
            'message': line,
            'hostname': 'docker-host',
            'program': container_name
        }
    
    def watch_container(self, container_id: str):
        """Watch logs from a specific container"""
        try:
            container = self.client.containers.get(container_id)
            container_name = container.name
            
            # Check if container is excluded
            if self.is_container_excluded(container_name):
                print(f"[DockerLogCollector] Skipping excluded container: {container_name}")
                if container_id in self.watched_containers:
                    del self.watched_containers[container_id]
                return
            
           # print(f"[DockerLogCollector] Watching container: {container_name}")
            
            # Stream logs from now
            for log in container.logs(stream=True, follow=True, since=int(time.time())):
                if not self.running:
                    break
                
                # Check if Docker collection is still enabled
                if not self.is_enabled():
                    print(f"[DockerLogCollector] Docker collection disabled, stopping watch for: {container_name}")
                    break
                
                # Check if container was excluded while watching
                if self.is_container_excluded(container_name):
                    print(f"[DockerLogCollector] Container now excluded, stopping watch for: {container_name}")
                    break
                
                try:
                    line = log.decode('utf-8', errors='replace').strip()
                    if line:
                        log_entry = self.parse_log_line(line, container_name, container_id[:12])
                        if self.callback:
                            self.callback(log_entry)
                except Exception as e:
                    print(f"[DockerLogCollector] Error parsing log: {e}")
                    
        #except Exception as e:
         #   print(f"[DockerLogCollector] Error watching container {container_id}: {e}")
        finally:
            if container_id in self.watched_containers:
                del self.watched_containers[container_id]
    
    def watch_all_containers(self):
        """Watch logs from all running containers"""
        if not self.client:
            return
        
        while self.running:
            # Check if Docker collection is enabled
            if not self.is_enabled():
                time.sleep(10)
                continue
            
            try:
                containers = self.client.containers.list(filters={'status': 'running'})
                
                for container in containers:
                    # Skip excluded containers
                    if self.is_container_excluded(container.name):
                        continue
                    
                    if container.id not in self.watched_containers:
                        self.watched_containers[container.id] = True
                        thread = threading.Thread(
                            target=self.watch_container,
                            args=(container.id,),
                            daemon=True
                        )
                        thread.start()
                        self.threads.append(thread)
                
            except Exception as e:
                print(f"[DockerLogCollector] Error in watch loop: {e}")
            
            time.sleep(10)  # Check for new containers every 10 seconds
    
    def start(self):
        """Start the Docker log collector"""
        if self.running:
            return
        
        if not self.connect():
            print("[DockerLogCollector] Cannot start - Docker connection failed")
            return
        
        self.running = True
        
        # Start container watcher
        watcher_thread = threading.Thread(target=self.watch_all_containers, daemon=True)
        watcher_thread.start()
        self.threads.append(watcher_thread)
        
        print("[DockerLogCollector] Started")
    
    def stop(self):
        """Stop the Docker log collector"""
        self.running = False
        
        for thread in self.threads:
            thread.join(timeout=2)
        
        if self.client:
            self.client.close()
        
        print("[DockerLogCollector] Stopped")
    
    def get_container_logs(self, container_id: str, lines: int = 100) -> List[str]:
        """Get recent logs from a specific container"""
        if not self.client:
            return []
        
        try:
            container = self.client.containers.get(container_id)
            logs = container.logs(tail=lines, timestamps=True)
            return logs.decode('utf-8', errors='replace').split('\n')
        except Exception as e:
            print(f"[DockerLogCollector] Error getting logs: {e}")
            return []
