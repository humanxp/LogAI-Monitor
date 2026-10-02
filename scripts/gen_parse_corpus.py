"""Build a parse-comparison corpus from real records taken out of Redis.

For every sampled record the original syslog line is reconstructed, parsed with
the Python implementation, and written as JSONL for the C# parser to replay:

    {"input": "...", "ip": "...", "fields": {...}}

Two reconstruction paths, because the stored shape differs:

* RFC 5424 records keep the sender's timestamp, so the line is rebuilt
  faithfully as "<ts> <host> <app> <proc> <msgid> - <message>" and every field
  including the timestamp is comparable.
* RFC 3164 records store the *arrival* time in `timestamp` (the Python parser
  never assigns the 3164 timestamp text), so a synthetic 3164 timestamp is used
  and the timestamp field is left out of the comparison: the two
  implementations run at different moments and would never agree on "now".
"""

import json
import os
import sys

sys.path.insert(0, '/app')
import redis as redis_lib
from services.syslog_receiver import SyslogReceiver

COMPARED = ('source', 'source_type', 'hostname', 'program', 'facility',
            'severity', 'message', 'pid', 'proc_id', 'msg_id')

host = os.environ.get('REDIS_HOST', '127.0.0.1')
port = int(os.environ.get('REDIS_PORT', '6379'))
db = int(os.environ.get('REDIS_DB', '0'))
r = redis_lib.Redis(host=host, port=port, db=db, decode_responses=True,
                    socket_connect_timeout=5, socket_timeout=15)
print("redis %s:%d/%d ping=%s" % (host, port, db, r.ping()), flush=True)

recv = SyslogReceiver()
sample = int(sys.argv[1]) if len(sys.argv) > 1 else 3000
members = r.zrange('logs:timeline', -sample, -1)
print("sampled %d records" % len(members), flush=True)

written = rfc3164 = rfc5424 = 0
with open('/tmp/corpus.jsonl', 'w') as out:
    for member in members:
        record = r.hgetall(member)
        if not record:
            continue
        stamp = record.get('timestamp', '')
        ip = record.get('source', '0.0.0.0')
        hostname = record.get('hostname', '')
        program = record.get('program', 'unknown')
        message = record.get('message', '')
        is_5424 = 'T' in stamp and record.get('msg_id')

        if is_5424:
            raw = "%s %s %s %s %s - %s" % (
                stamp, hostname, program,
                record.get('proc_id', '-'), record.get('msg_id', '-'), message)
            rfc5424 += 1
            compared = COMPARED + ('timestamp',)
        else:
            raw = "Sep  7 05:23:00 %s %s%s: %s" % (
                hostname, program,
                "[%s]" % record['pid'] if record.get('pid') else '', message)
            rfc3164 += 1
            compared = COMPARED

        parsed = recv.parse_syslog_message(raw.encode('utf-8'), ip)
        fields = {k: parsed.get(k) for k in compared if parsed.get(k) is not None}
        out.write(json.dumps({'input': raw, 'ip': ip, 'fields': fields}) + '\n')
        written += 1

print("corpus: %d records (3164=%d 5424=%d)" % (written, rfc3164, rfc5424), flush=True)
