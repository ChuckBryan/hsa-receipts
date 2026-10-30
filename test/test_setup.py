import importlib.util
import pathlib
import unittest
spec=importlib.util.spec_from_file_location('setup',pathlib.Path(__file__).resolve().parents[1]/'setup.py')
setup=importlib.util.module_from_spec(spec);spec.loader.exec_module(setup)
class ConfigurationTests(unittest.TestCase):
    def test_oidc_contract(self):
        env,realm=setup.generate('hsa.example.com','auth.example.com',['chuck','cindy'],'172.17.0.1')
        client=realm['clients'][0]
        self.assertFalse(realm['registrationAllowed'])
        self.assertFalse(client['publicClient'])
        self.assertFalse(client['directAccessGrantsEnabled'])
        self.assertEqual(env['APP_IMAGE'],'hsa-receipts-app:latest')
        self.assertEqual(client['secret'],env['APP_CLIENT_SECRET'])
        self.assertEqual(client['redirectUris'],['https://hsa.example.com/signin-oidc'])
        self.assertEqual(client['attributes']['pkce.code.challenge.method'],'S256')
        self.assertEqual(len(realm['users']),2)
        self.assertTrue(all(u['credentials'][0]['temporary'] for u in realm['users']))
    def test_secrets_are_unique(self):
        a,r=setup.generate('a.example.com','b.example.com',['one','two'],'127.0.0.1')
        b,_=setup.generate('a.example.com','b.example.com',['one','two'],'127.0.0.1')
        self.assertNotEqual(a['APP_CLIENT_SECRET'],b['APP_CLIENT_SECRET'])
        self.assertEqual(len({a['APP_CLIENT_SECRET'],a['POSTGRES_PASSWORD'],a['KEYCLOAK_ADMIN_PASSWORD'],*[u['credentials'][0]['value'] for u in r['users']]}),5)
if __name__=='__main__': unittest.main()
