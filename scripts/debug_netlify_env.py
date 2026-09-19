import os
#!/usr/bin/env python3
"""Debug Netlify API endpoints to find the right one for env vars."""
import json
import urllib.request
import urllib.error

NETLIFY_TOKEN = os.environ.get('NETLIFY_TOKEN', '')
SITE_ID = 'f93838f6-3f8d-42fd-8d7a-a557a2a76fac'
SITE_SLUG = 'dashboards-groq-proxy'
GROQ_KEY = os.environ.get('GROQ_KEY', '')

def req(method, url, body=None, raw=False):
    hdrs = {
        'Authorization': 'Bearer ' + NETLIFY_TOKEN,
        'User-Agent': 'dashboards-deploy/1.0',
    }
    data = None
    if body is not None:
        if not raw:
            data = json.dumps(body).encode()
            hdrs['Content-Type'] = 'application/json'
        else:
            data = body
            hdrs['Content-Type'] = 'application/octet-stream'
    r = urllib.request.Request(url, data=data, method=method, headers=hdrs)
    try:
        resp = urllib.request.urlopen(r, timeout=30)
        return resp.status, resp.read().decode()[:500]
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()[:500]
    except Exception as e:
        return 0, str(e)[:500]

print('=== Try GET /sites/{slug}/env ===')
s, b = req('GET', f'https://api.netlify.com/api/v1/sites/{SITE_SLUG}/env')
print(f'  {s}: {b}')

print('\n=== Try GET /sites/{site_id}/env ===')
s, b = req('GET', f'https://api.netlify.com/api/v1/sites/{SITE_ID}/env')
print(f'  {s}: {b}')

print('\n=== Try POST /sites/{site_id}/env with scopes field ===')
s, b = req('POST', f'https://api.netlify.com/api/v1/sites/{SITE_ID}/env', body={
    'key': 'GROQ_API_KEY',
    'scopes': ['build', 'functions', 'runtime', 'post_processing'],
    'values': [{'value': GROQ_KEY, 'context': 'all'}],
})
print(f'  {s}: {b}')

print('\n=== Try POST /sites/{slug}/env with scopes field ===')
s, b = req('POST', f'https://api.netlify.com/api/v1/sites/{SITE_SLUG}/env', body={
    'key': 'GROQ_API_KEY',
    'scopes': ['build', 'functions', 'runtime', 'post_processing'],
    'values': [{'value': GROQ_KEY, 'context': 'all'}],
})
print(f'  {s}: {b}')

print('\n=== Try GET /sites/{slug} (verify slug works) ===')
s, b = req('GET', f'https://api.netlify.com/api/v1/sites/{SITE_SLUG}')
print(f'  {s}: {b[:200]}')

print('\n=== Try GET /sites/{site_id} (verify id works) ===')
s, b = req('GET', f'https://api.netlify.com/api/v1/sites/{SITE_ID}')
print(f'  {s}: {b[:200]}')
