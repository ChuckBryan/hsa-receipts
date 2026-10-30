#!/usr/bin/env python3
"""Create fresh deployment configuration. Never overwrites existing secrets."""
import json, os, re, secrets
from pathlib import Path

def hostname(prompt):
    value = input(prompt).strip().lower()
    if not re.fullmatch(r'[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?', value) or '.' not in value:
        raise SystemExit('Enter a hostname such as hsa.example.com, without https:// or a path.')
    return value

def generate(app, auth, users, proxies):
    client_secret = secrets.token_urlsafe(40)
    env = {'APP_IMAGE':'hsa-receipts-app:latest', 'APP_HOST':app, 'AUTH_HOST':auth,
           'ALLOWED_USERS':','.join(users), 'TRUSTED_PROXIES':proxies,
           'APP_CLIENT_SECRET':client_secret, 'POSTGRES_PASSWORD':secrets.token_urlsafe(40),
           'KEYCLOAK_ADMIN_PASSWORD':secrets.token_urlsafe(30)}
    realm = {'realm':'household','enabled':True,'sslRequired':'external', 'registrationAllowed':False,
             'resetPasswordAllowed':False,'rememberMe':True,'bruteForceProtected':True,
             'clients':[{'clientId':'hsa-receipts','name':'Receipt Keep','enabled':True,'protocol':'openid-connect',
                         'publicClient':False,'secret':client_secret,'standardFlowEnabled':True,
                         'implicitFlowEnabled':False,'directAccessGrantsEnabled':False,
                         'redirectUris':[f'https://{app}/signin-oidc'],
                         'attributes':{'pkce.code.challenge.method':'S256',
                                       'post.logout.redirect.uris':f'https://{app}/signout-callback-oidc'},
                         'defaultClientScopes':['profile','email']}],
             'users':[{'username':user,'enabled':True,'requiredActions':['UPDATE_PASSWORD'],
                       'credentials':[{'type':'password','value':secrets.token_urlsafe(25),'temporary':True}]} for user in users]}
    return env, realm

def main():
    os.chdir(Path(__file__).resolve().parent)
    if Path('.env').exists() or Path('deploy/realm.json').exists():
        raise SystemExit('Configuration already exists. Edit it in place; setup will not replace secrets.')
    app=hostname('Receipt app hostname (hsa.example.com): ')
    auth=hostname('Keycloak hostname (auth.example.com): ')
    if app == auth: raise SystemExit('Use separate app and authentication hostnames.')
    users=input('Two usernames, comma separated [chuck,cindy]: ').strip() or 'chuck,cindy'
    users=[u.strip().lower() for u in users.split(',')]
    if len(users)!=2 or len(set(users))!=2 or any(not re.fullmatch(r'[a-z0-9_-]{1,40}',u) for u in users):
        raise SystemExit('Provide exactly two different simple usernames.')
    import ipaddress
    proxies=input('Trusted proxy IP as seen inside app container (see README): ').strip()
    try:
        proxies=','.join(str(ipaddress.ip_address(p.strip())) for p in proxies.split(','))
    except ValueError: raise SystemExit('Enter one or more exact proxy IP addresses, comma separated.')
    env,realm=generate(app,auth,users,proxies)
    Path('deploy').mkdir(exist_ok=True)
    for path,content in [(Path('.env'),'\n'.join(f'{k}={v}' for k,v in env.items())+'\n'),
                         (Path('deploy/realm.json'),json.dumps(realm,indent=2)+'\n')]:
        fd=os.open(path,os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600)
        with os.fdopen(fd,'w') as f: f.write(content)
    # Keycloak runs as uid 1000; imported realm must be readable inside container.
    # Keep containing folder restricted on NAS; this file contains client/user secrets.
    os.chmod('deploy/realm.json',0o644)
    Path('data/app').mkdir(parents=True,exist_ok=True)
    print('Configuration created. Set data/app ownership for uid 1654 before starting (see README).')
    print('Temporary household passwords are in deploy/realm.json; each user must change theirs at first sign-in.')
    print('Keycloak bootstrap password is in .env. Do not share these files or include them in source control.')
if __name__=='__main__': main()
