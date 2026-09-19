import os
#!/usr/bin/env python3
"""Try more variations to find the right Netlify env var API."""
import json
import urllib.request
import urllib.error

NETLIFY_TOKEN = os.environ.get('NETLIFY_TOKEN', '')
SITE_ID = 'f93838f6-3f8d-42fd-8d7a-a557a2a76fac'
GROQ_KEY = os.environ.get('GROQ_KEY', '')

def req(method, url, body=None, content_type='application/json'):
    hdrs = {
        'Authorization': 'Bearer ' + NETLIFY_TOKEN,
        'User-Agent': 'dashboards-deploy/1.0',
        'Content-Type': content_type,
    }
    data = json.dumps(body).encode() if body else None
    r = urllib.request.Request(url, data=data, method=method, headers=hdrs)
    try:
        resp = urllib.request.urlopen(r, timeout=30)
        return resp.status, resp.read().decode()[:500], dict(resp.headers)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()[:500], dict(e.headers)
    except Exception as e:
        return 0, str(e)[:500], {}

# Try 1: POST with simpler body (no scopes, no context)
print('=== Try 1: POST /sites/{id}/env — minimal body ===')
s, b, h = req('POST', f'https://api.netlify.com/api/v1/sites/{SITE_ID}/env', body={
    'key': 'GROQ_API_KEY',
    'values': [{'value': GROQ_KEY}],
})
print(f'  {s}: {b}')
if 'Allow' in h: print(f'  Allow header: {h["Allow"]}')

# Try 2: PUT to /sites/{id}/env/{key}
print('\n=== Try 2: PUT /sites/{id}/env/GROQ_API_KEY ===')
s, b, h = req('PUT', f'https://api.netlify.com/api/v1/sites/{SITE_ID}/env/GROQ_API_KEY', body={
    'key': 'GROQ_API_KEY',
    'scopes': ['build', 'functions', 'runtime'],
    'values': [{'value': GROQ_KEY, 'context': 'all'}],
})
print(f'  {s}: {b}')

# Try 3: POST to /sites/{id}/env-vars (hyphen)
print('\n=== Try 3: POST /sites/{id}/env-vars ===')
s, b, h = req('POST', f'https://api.netlify.com/api/v1/sites/{SITE_ID}/env-vars', body={
    'key': 'GROQ_API_KEY',
    'values': [{'value': GROQ_KEY, 'context': 'all'}],
})
print(f'  {s}: {b}')

# Try 4: PATCH /sites/{id} with env field (the legacy approach)
print('\n=== Try 4: PATCH /sites/{id} with env field ===')
s, b, h = req('PATCH', f'https://api.netlify.com/api/v1/sites/{SITE_ID}', body={
    'env': {'GROQ_API_KEY': GROQ_KEY},
})
print(f'  {s}: {b[:300]}')

# Try 5: OPTIONS /sites/{id}/env to see allowed methods
print('\n=== Try 5: OPTIONS /sites/{id}/env ===')
s, b, h = req('OPTIONS', f'https://api.netlify.com/api/v1/sites/{SITE_ID}/env')
print(f'  {s}: {b[:200]}')
if 'Allow' in h: print(f'  Allow: {h["Allow"]}')
if 'access-control-allow-methods' in h: print(f'  CORS methods: {h["access-control-allow-methods"]}')

# Try 6: POST with site_slug in body and site_id in URL (Netlify's weird hybrid)
print('\n=== Try 6: POST with scopes as comma-separated string ===')
s, b, h = req('POST', f'https://api.netlify.com/api/v1/sites/{SITE_ID}/env', body={
    'key': 'GROQ_API_KEY',
    'scopes': 'build,functions,runtime',
    'values': [{'value': GROQ_KEY, 'context': 'all'}],
})
print(f'  {s}: {b}')
