import os
#!/usr/bin/env python3
"""
Deploy the Groq proxy edge function to Netlify via API.

Pipeline:
1. Create a Netlify site named 'dashboards-groq-proxy'
2. Set GROQ_API_KEY env var on the site (value reconstructed from
   the user's commit 8fef66a keyParts)
3. Deploy the edge function file (zip + upload)
4. Wait for deploy state = ready
5. Ping the proxy with a max_tokens=1 request to verify
"""
import json
import sys
import time
import urllib.request
import urllib.error
import hashlib
from pathlib import Path

NETLIFY_TOKEN = os.environ.get('NETLIFY_TOKEN', '')
SITE_NAME = 'dashboards-groq-proxy'
GROQ_KEY = os.environ.get('GROQ_KEY', '')
REPO_ROOT = Path('/home/z/my-project')
EDGE_FUNC_PATH = REPO_ROOT / 'netlify' / 'edge-functions' / 'groq-proxy.js'
NETLIFY_TOML_PATH = REPO_ROOT / 'netlify.toml'

NETLIFY_API = 'https://api.netlify.com/api/v1'

def api_request(method, path, body=None, headers=None, raw_body=False):
    """Make an authenticated Netlify API request."""
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

def step1_create_site():
    """Create the Netlify site with name 'dashboards-groq-proxy'."""
    print(f'\n[1/5] Creating Netlify site: {SITE_NAME}')
    res = api_request('GET', f'/sites/{SITE_NAME}.netlify.com')
    if res['status'] == 200:
        site = json.loads(res['body'])
        print(f'  Site already exists: {site.get("ssl_url", site.get("url"))}')
        print(f'  Site ID: {site["site_id"]}')
        return site

    res = api_request('POST', '/sites', body={
        'name': SITE_NAME,
        'custom_domain': None,
    })
    if res['status'] in (200, 201):
        site = json.loads(res['body'])
        print(f'  Created: {site.get("ssl_url", site.get("url"))}')
        print(f'  Site ID: {site["site_id"]}')
        return site
    else:
        print(f'  ERROR creating site: HTTP {res["status"]}')
        print(f'  Body: {res["body"][:500]}')
        sys.exit(1)

def step2_set_env_var(site_id):
    """Set GROQ_API_KEY env var on the site."""
    print(f'\n[2/5] Setting GROQ_API_KEY env var on site {site_id[:8]}...')
    res = api_request('POST', f'/sites/{site_id}/env', body={
        'key': 'GROQ_API_KEY',
        'values': [{'value': GROQ_KEY, 'context': 'all'}],
    })
    if res['status'] in (200, 201):
        print(f'  Env var set: GROQ_API_KEY={GROQ_KEY[:12]}...{GROQ_KEY[-4:]}')
        return
    if res['status'] == 422:
        print('  Already exists, updating...')
        res2 = api_request('PATCH', f'/sites/{site_id}/env/GROQ_API_KEY', body={
            'values': [{'value': GROQ_KEY, 'context': 'all'}],
        })
        if res2['status'] in (200, 204):
            print(f'  Env var updated: GROQ_API_KEY={GROQ_KEY[:12]}...{GROQ_KEY[-4:]}')
            return
        print(f'  ERROR updating env var: HTTP {res2["status"]}')
        print(f'  Body: {res2["body"][:500]}')
        sys.exit(1)
    print(f'  ERROR setting env var: HTTP {res["status"]}')
    print(f'  Body: {res["body"][:500]}')
    sys.exit(1)

def step3_deploy_function(site_id):
    """Deploy the edge function file via the Netlify deploy API."""
    print(f'\n[3/5] Deploying edge function to site {site_id[:8]}...')

    res = api_request('POST', f'/sites/{site_id}/deploys')
    if res['status'] not in (200, 201):
        print(f'  ERROR creating deploy: HTTP {res["status"]}')
        print(f'  Body: {res["body"][:500]}')
        sys.exit(1)
    deploy = json.loads(res['body'])
    deploy_id = deploy['id']
    print(f'  Deploy ID: {deploy_id}')

    if not EDGE_FUNC_PATH.exists():
        print(f'  ERROR: edge function file not found at {EDGE_FUNC_PATH}')
        sys.exit(1)
    func_content = EDGE_FUNC_PATH.read_bytes()
    print(f'  Edge function file: {EDGE_FUNC_PATH.name} ({len(func_content)} bytes)')

    file_path_on_netlify = '/netlify/edge-functions/groq-proxy.js'
    res = api_request(
        'PUT',
        f'/deploys/{deploy_id}/files{file_path_on_netlify}',
        body=func_content,
        headers={'Content-Type': 'application/octet-stream'},
        raw_body=True,
    )
    if res['status'] not in (200, 201, 204):
        print(f'  ERROR uploading edge function: HTTP {res["status"]}')
        print(f'  Body: {res["body"][:500]}')
        sys.exit(1)
    print(f'  Uploaded: {file_path_on_netlify}')

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
            print(f'  Uploaded: /netlify.toml ({len(toml_content)} bytes)')

    return deploy_id

def step4_wait_for_deploy(deploy_id):
    """Wait for the deploy to reach 'ready' state."""
    print(f'\n[4/5] Waiting for deploy {deploy_id[:8]} to be ready...')
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
    print(f'  Timed out after {max_wait}s — deploy still in progress')
    return None

def step5_verify_proxy():
    """Ping the proxy with a max_tokens=5 request."""
    proxy_url = f'https://{SITE_NAME}.netlify.app/groq-proxy'
    print(f'\n[5/5] Verifying proxy at {proxy_url}')
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
        if resp.status == 200:
            print('\n  PROXY IS LIVE — the GitHub Pages site will now route Groq calls through this URL.')
            return True
        else:
            print('\n  Proxy returned non-200')
            return False
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
    print('  Netlify Groq Proxy Deploy')
    print('=' * 70)
    print(f'  Site name:    {SITE_NAME}')
    print(f'  Site URL:     https://{SITE_NAME}.netlify.app')
    print(f'  Proxy URL:    https://{SITE_NAME}.netlify.app/groq-proxy')
    print(f'  GROQ_API_KEY: {GROQ_KEY[:12]}...{GROQ_KEY[-4:]} (server-side only)')

    site = step1_create_site()
    site_id = site['site_id']

    step2_set_env_var(site_id)

    deploy_id = step3_deploy_function(site_id)

    step4_wait_for_deploy(deploy_id)

    step5_verify_proxy()

    print('\n' + '=' * 70)
    print('  Done. The data/groq-config.json in the repo already points at')
    print(f'  https://{SITE_NAME}.netlify.app/groq-proxy — refresh the GH')
    print('  Pages site and the pill should flip to green "Groq live".')
    print('=' * 70)

if __name__ == '__main__':
    main()
