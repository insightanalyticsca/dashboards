import os
#!/usr/bin/env python3
"""
Deploy edge function — corrected to use SHA1 hash in upload URL.
Netlify's API: PUT /deploys/{id}/files/{sha1_hash}  (NOT /files/{path})
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

def main():
    print('=' * 70)
    print('  Netlify Edge Function Deploy (SHA1-keyed uploads)')
    print('=' * 70)

    # Compute SHA1s
    func_content = EDGE_FUNC_PATH.read_bytes()
    func_path = '/netlify/edge-functions/groq-proxy.js'
    func_sha1 = hashlib.sha1(func_content).hexdigest()
    print(f'Edge function: {len(func_content)} bytes')
    print(f'  path: {func_path}')
    print(f'  sha1: {func_sha1}')

    toml_content = NETLIFY_TOML_PATH.read_bytes() if NETLIFY_TOML_PATH.exists() else b''
    toml_path = '/netlify.toml'
    toml_sha1 = hashlib.sha1(toml_content).hexdigest() if toml_content else None
    if toml_sha1:
        print(f'netlify.toml: {len(toml_content)} bytes, sha1={toml_sha1}')

    # Map of sha1 → file content (for lookup during upload)
    sha_to_content = {func_sha1: func_content}
    if toml_sha1:
        sha_to_content[toml_sha1] = toml_content

    # Step 1: Create deploy with file digest map
    print('\n[1] Creating deploy with file digests...')
    files_map = {func_path: func_sha1}
    if toml_sha1:
        files_map[toml_path] = toml_sha1

    res = api_request('POST', f'/sites/{SITE_ID}/deploys', body={
        'files': files_map,
    })
    if res['status'] not in (200, 201):
        print(f'ERROR: HTTP {res["status"]}')
        print(f'Body: {res["body"][:500]}')
        sys.exit(1)
    deploy = json.loads(res['body'])
    deploy_id = deploy['id']
    print(f'Deploy ID: {deploy_id}')
    print(f'State: {deploy.get("state")}')

    required = deploy.get('required', [])
    print(f'Files required: {len(required)}')
    for sha in required:
        print(f'  - {sha}')

    # Step 2: Upload each required file (using SHA1 as URL param, NOT path)
    print('\n[2] Uploading files...')
    for sha in required:
        content = sha_to_content.get(sha)
        if content is None:
            print(f'  SKIP unknown sha: {sha}')
            continue
        res = api_request(
            'PUT',
            f'/deploys/{deploy_id}/files/{sha}',
            body=content,
            headers={'Content-Type': 'application/octet-stream'},
            raw_body=True,
        )
        print(f'  Upload {sha[:12]}... ({len(content)} bytes): HTTP {res["status"]}')
        if res['status'] not in (200, 201, 204):
            print(f'    Body: {res["body"][:300]}')
            sys.exit(1)

    # Step 3: Wait for ready
    print('\n[3] Waiting for deploy to be ready...')
    max_wait = 90
    poll_interval = 3
    waited = 0
    final_state = None
    while waited < max_wait:
        res = api_request('GET', f'/deploys/{deploy_id}')
        if res['status'] == 200:
            deploy = json.loads(res['body'])
            state = deploy.get('state') or deploy.get('status')
            print(f'  [{waited}s] state={state}')
            final_state = state
            if state == 'ready':
                break
            if state in ('error', 'rejected'):
                print(f'  FAILED: {state}')
                sys.exit(1)
        time.sleep(poll_interval)
        waited += poll_interval

    # Step 4: Verify proxy
    print(f'\n[4] Verifying proxy at https://{SITE_NAME}.netlify.app/groq-proxy')
    proxy_url = f'https://{SITE_NAME}.netlify.app/groq-proxy'
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
        print(f'  Response: {data[:500]}')
    except urllib.error.HTTPError as e:
        body_text = e.read().decode()
        print(f'  HTTP {e.code}')
        print(f'  Body: {body_text[:500]}')
    except Exception as e:
        print(f'  Network error: {e}')

    print('\n' + '=' * 70)
    print(f'  Final deploy state: {final_state}')
    print(f'  Proxy URL: https://{SITE_NAME}.netlify.app/groq-proxy')
    print()
    print('  NEXT STEPS:')
    print('  1. Set GROQ_API_KEY env var on Netlify (dashboard):')
    print('     https://app.netlify.com/sites/dashboards-groq-proxy/settings/env')
    print('     Key: GROQ_API_KEY')
    print('     Value: <paste your Groq key from https://console.groq.com/keys>')
    print('     Scopes: Build + Functions')
    print('  2. Trigger redeploy (Clear cache and deploy)')
    print('  3. Refresh https://insightanalyticsca.github.io/dashboards/')
    print('=' * 70)

if __name__ == '__main__':
    main()
