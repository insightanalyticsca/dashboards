import os
#!/usr/bin/env python3
"""
Deploy the edge function only (skip env var — user will set manually via UI).
"""
import json
import sys
import time
import urllib.request
import urllib.error
from pathlib import Path

NETLIFY_TOKEN = os.environ.get('NETLIFY_TOKEN', '')
SITE_ID = 'f93838f6-3f8d-42fd-8d7a-a557a2a76fac'
SITE_NAME = 'dashboards-groq-proxy'
REPO_ROOT = Path('/home/z/my-project')
EDGE_FUNC_PATH = REPO_ROOT / 'netlify' / 'edge-functions' / 'groq-proxy.js'
NETLIFY_TOML_PATH = REPO_ROOT / 'netlify.toml'

NETLIFY_API = 'https://api.netlify.com/api/v1'

def api_request(method, path, body=None, headers=None, raw_body=False):
    url = NETLIFY_API + path if path.startswith('/') else path
    hdrs = {
        'Authorization': 'Bearer ' + NETLIFY_TOKEN,
        'User-Agent': 'dashboards-deploy/1.0',
    }
    if headers:
        hdrs.update(headers)
    data = None
    if body is not None:
        if not raw_body:
            data = json.dumps(body).encode()
            hdrs['Content-Type'] = 'application/json'
        else:
            data = body
            if 'Content-Type' not in hdrs:
                hdrs['Content-Type'] = 'application/octet-stream'
    req = urllib.request.Request(url, data=data, method=method, headers=hdrs)
    try:
        resp = urllib.request.urlopen(req, timeout=60)
        body_out = resp.read().decode() if resp.status != 204 else ''
        return {'status': resp.status, 'body': body_out, 'headers': dict(resp.headers)}
    except urllib.error.HTTPError as e:
        return {'status': e.code, 'body': e.read().decode(), 'headers': dict(e.headers)}
    except Exception as e:
        return {'status': 0, 'body': str(e), 'headers': {}}

def deploy_function():
    print(f'\nDeploying edge function to site {SITE_ID[:8]}...')

    # Create a new deploy
    res = api_request('POST', f'/sites/{SITE_ID}/deploys')
    if res['status'] not in (200, 201):
        print(f'ERROR creating deploy: HTTP {res["status"]}')
        print(f'Body: {res["body"][:500]}')
        sys.exit(1)
    deploy = json.loads(res['body'])
    deploy_id = deploy['id']
    print(f'Deploy ID: {deploy_id}')

    # Upload the edge function file
    func_content = EDGE_FUNC_PATH.read_bytes()
    print(f'Edge function file: {len(func_content)} bytes')

    file_path = '/netlify/edge-functions/groq-proxy.js'
    res = api_request(
        'PUT',
        f'/deploys/{deploy_id}/files{file_path}',
        body=func_content,
        headers={'Content-Type': 'application/octet-stream'},
        raw_body=True,
    )
    if res['status'] not in (200, 201, 204):
        print(f'ERROR uploading edge function: HTTP {res["status"]}')
        print(f'Body: {res["body"][:500]}')
        sys.exit(1)
    print(f'Uploaded: {file_path}')

    # Also upload netlify.toml
    if NETLIFY_TOML_PATH.exists():
        toml_content = NETLIFY_TOML_PATH.read_bytes()
        res = api_request(
            'PUT',
            f'/deploys/{deploy_id}/files/netlify.toml',
            body=toml_content,
            headers={'Content-Type': 'application/octet-stream'},
            raw_body=True,
        )
        if res['status'] in (200, 201, 204):
            print(f'Uploaded: /netlify.toml ({len(toml_content)} bytes)')

    return deploy_id

def wait_for_ready(deploy_id):
    print(f'\nWaiting for deploy {deploy_id[:8]} to be ready...')
    max_wait = 120
    poll_interval = 3
    waited = 0
    while waited < max_wait:
        res = api_request('GET', f'/deploys/{deploy_id}')
        if res['status'] == 200:
            deploy = json.loads(res['body'])
            state = deploy.get('state') or deploy.get('status')
            print(f'  [{waited}s] state={state}')
            if state == 'ready':
                print('  Deploy is ready')
                return deploy
            if state in ('error', 'rejected'):
                print(f'  Deploy failed: {state}')
                sys.exit(1)
        time.sleep(poll_interval)
        waited += poll_interval
    print(f'  Timed out after {max_wait}s')
    return None

def verify_proxy():
    proxy_url = f'https://{SITE_NAME}.netlify.app/groq-proxy'
    print(f'\nPinging proxy at {proxy_url}')
    body = json.dumps({
        'model': 'llama-3.3-70b-versatile',
        'messages': [{'role': 'user', 'content': 'ping'}],
        'max_tokens': 5,
        'stream': False,
    }).encode()
    req = urllib.request.Request(
        proxy_url,
        data=body,
        method='POST',
        headers={
            'Content-Type': 'application/json',
            'Origin': 'https://insightanalyticsca.github.io',
        },
    )
    try:
        resp = urllib.request.urlopen(req, timeout=30)
        data = resp.read().decode()
        print(f'  HTTP {resp.status}')
        print(f'  Response: {data[:400]}')
        return resp.status == 200
    except urllib.error.HTTPError as e:
        body_text = e.read().decode()
        print(f'  HTTP {e.code}')
        print(f'  Body: {body_text[:400]}')
        return False
    except Exception as e:
        print(f'  Network error: {e}')
        return False

def main():
    print('=' * 70)
    print('  Netlify Edge Function Deploy (function only)')
    print('=' * 70)

    deploy_id = deploy_function()
    wait_for_ready(deploy_id)

    print('\nVerifying proxy (expect HTTP 500 — env var not set yet):')
    verify_proxy()

    print('\n' + '=' * 70)
    print('  Edge function deployed to:')
    print(f'    https://{SITE_NAME}.netlify.app/groq-proxy')
    print()
    print('  NEXT STEP (user — 30 seconds):')
    print('  1. Open https://app.netlify.com/sites/dashboards-groq-proxy/settings/env')
    print('  2. Click "Add a variable" → Key: GROQ_API_KEY')
    print('  3. Value: paste the Groq key (<paste your Groq key>)')
    print('  4. Scopes: Functions + Build')
    print('  5. Save')
    print('  6. Go to Deploys → Trigger deploy → Clear cache and deploy')
    print()
    print('  After redeploy, refresh https://insightanalyticsca.github.io/dashboards/')
    print('  — the pill should flip to green "Groq live".')
    print('=' * 70)

if __name__ == '__main__':
    main()
