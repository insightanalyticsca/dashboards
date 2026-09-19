import os
#!/usr/bin/env python3
"""
Deploy the edge function with the proper Netlify deploy flow:
1. Compute SHA1 of the edge function file
2. POST /sites/{site_id}/deploys with body containing {files: {path: sha1}}
   — this returns a deploy with a 'required' field listing files that need upload
3. For each required file: PUT /deploys/{deploy_id}/files/{path} with content
4. Wait for state = ready
5. Verify the proxy responds (expect 500 — env var not set yet)
"""
import json
import sys
import time
import urllib.request
import urllib.error
import hashlib
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

def deploy_function_proper():
    """Deploy using the file-digest flow."""
    print(f'\nDeploying edge function to site {SITE_ID[:8]}...')

    # Read files and compute SHA1
    func_content = EDGE_FUNC_PATH.read_bytes()
    func_path = '/netlify/edge-functions/groq-proxy.js'
    func_sha1 = hashlib.sha1(func_content).hexdigest()
    print(f'Edge function: {len(func_content)} bytes, SHA1={func_sha1[:12]}...')

    toml_content = NETLIFY_TOML_PATH.read_bytes() if NETLIFY_TOML_PATH.exists() else b''
    toml_path = '/netlify.toml'
    toml_sha1 = hashlib.sha1(toml_content).hexdigest() if toml_content else None

    # Step 1: Create the deploy with file digests
    files_map = {func_path: func_sha1}
    if toml_sha1:
        files_map[toml_path] = toml_sha1

    res = api_request('POST', f'/sites/{SITE_ID}/deploys', body={
        'files': files_map,
        'async': False,
        'draft': False,
    })
    if res['status'] not in (200, 201):
        print(f'ERROR creating deploy: HTTP {res["status"]}')
        print(f'Body: {res["body"][:500]}')
        sys.exit(1)

    deploy = json.loads(res['body'])
    deploy_id = deploy['id']
    print(f'Deploy ID: {deploy_id}')
    print(f'Initial state: {deploy.get("state") or deploy.get("status")}')

    required = deploy.get('required', [])
    print(f'Files Netlify wants uploaded: {required}')

    # Step 2: Upload each required file
    if func_path in required:
        res = api_request(
            'PUT',
            f'/deploys/{deploy_id}/files{func_path}',
            body=func_content,
            headers={'Content-Type': 'application/octet-stream'},
            raw_body=True,
        )
        print(f'Upload {func_path}: HTTP {res["status"]}')
        if res['status'] not in (200, 201, 204):
            print(f'  Body: {res["body"][:300]}')
            sys.exit(1)

    if toml_path in required and toml_content:
        res = api_request(
            'PUT',
            f'/deploys/{deploy_id}/files{toml_path}',
            body=toml_content,
            headers={'Content-Type': 'application/octet-stream'},
            raw_body=True,
        )
        print(f'Upload {toml_path}: HTTP {res["status"]}')
        if res['status'] not in (200, 201, 204):
            print(f'  Body: {res["body"][:300]}')

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
                print(f'  Body: {res["body"][:500]}')
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
    print('  Netlify Edge Function Deploy (proper file-digest flow)')
    print('=' * 70)

    deploy_id = deploy_function_proper()
    wait_for_ready(deploy_id)

    print('\nVerifying proxy (expect HTTP 500 — env var not set yet):')
    verify_proxy()

    print('\n' + '=' * 70)
    print('  Edge function deployed to:')
    print(f'    https://{SITE_NAME}.netlify.app/groq-proxy')
    print()
    print('  NEXT STEP (user — 30 seconds):')
    print('  1. Open https://app.netlify.com/sites/dashboards-groq-proxy/settings/env')
    print('  2. Add variable → Key: GROQ_API_KEY → Value: <paste your Groq key>')
    print('  3. Scopes: Functions + Build  →  Save')
    print('  4. Deploys → Trigger deploy → Clear cache and deploy')
    print()
    print('  After redeploy, refresh https://insightanalyticsca.github.io/dashboards/')
    print('  — the pill should flip to green "Groq live".')
    print('=' * 70)

if __name__ == '__main__':
    main()
