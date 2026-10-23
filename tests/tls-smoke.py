from pathlib import Path
import contextlib, http.client, json, os, socket, ssl, subprocess, tempfile, time
root=Path(__file__).resolve().parents[1]
os.chdir(root)
(root/'work').mkdir(exist_ok=True)
with tempfile.TemporaryDirectory(prefix='tls-', dir=root/'work') as td:
 p=Path(td)
 address=subprocess.check_output(['hostname','-I'],text=True).split()[0]
 subprocess.run(['openssl','req','-x509','-newkey','rsa:2048','-nodes','-keyout',str(p/'key.pem'),'-out',str(p/'cert.pem'),'-days','1','-subj','/CN=ClubTest','-addext',f'subjectAltName=IP:{address}'],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
 subprocess.run(['openssl','pkcs12','-export','-inkey',str(p/'key.pem'),'-in',str(p/'cert.pem'),'-out',str(p/'cert.pfx'),'-passout','pass:'],check=True)
 env=os.environ.copy();env.update({'Club__DataDirectory':str(p/'data'),'Club__Lan__Enabled':'true','Club__Lan__Address':address,'Club__Lan__Port':'55443','Club__Lan__CertificatePath':str(p/'cert.pfx'),'AllowedHosts':f'localhost;127.0.0.1;{address}'})
 with (p/'server.log').open('w') as log:
  process=subprocess.Popen(['bash','scripts/dotnet.sh','src/Club.Server/bin/Release/net10.0/Club.Server.dll'],env=env,stdout=log,stderr=log)
  try:
   for _ in range(100):
    if process.poll() is not None: raise RuntimeError((p/'server.log').read_text())
    try:
     c=http.client.HTTPConnection('127.0.0.1',5080,timeout=1);c.request('GET','/health');r=c.getresponse();assert r.status==200;r.read();c.close();break
    except (OSError, AssertionError): time.sleep(.1)
   else: raise RuntimeError('Server did not start')
   try:
    c=http.client.HTTPSConnection(address,55443,timeout=3);c.request('GET','/health');c.getresponse();raise AssertionError('Untrusted TLS unexpectedly accepted')
   except ssl.SSLCertVerificationError: pass
   context=ssl.create_default_context(cafile=str(p/'cert.pem'))
   c=http.client.HTTPSConnection(address,55443,context=context,timeout=3)
   c.request('GET','/health');r=c.getresponse();assert r.status==200;r.read()
   c.request('POST','/device/heartbeat',body=json.dumps({'bootId':'00000000-0000-0000-0000-000000000001','sequence':1,'sessionId':None,'revision':0,'remainingMs':0,'state':'Idle'}),headers={'Content-Type':'application/json'})
   r=c.getresponse();assert r.status==401;r.read();c.close()
   c=http.client.HTTPConnection(address,5080,timeout=1)
   try: c.connect();raise AssertionError('Loopback server exposed on LAN')
   except (ConnectionRefusedError, TimeoutError): pass
   print('PASS: real Kestrel TLS listener, trusted certificate accepted, untrusted certificate rejected, missing device key rejected, plaintext listener bound only to loopback.')
  finally:
   process.terminate()
   try: process.wait(timeout=10)
   except subprocess.TimeoutExpired: process.kill();process.wait()
