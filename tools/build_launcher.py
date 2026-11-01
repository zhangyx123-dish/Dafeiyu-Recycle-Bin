from pathlib import Path
import base64,hashlib,io,json,zipfile,textwrap
root=Path(__file__).resolve().parents[1]
out=root/'downloads'/'RecyclePet-Windows-v0.5.cmd'
selected={p.relative_to(root).as_posix():p.read_bytes() for p in root.rglob('*') if p.is_file() and (p.relative_to(root).as_posix() in ('assets/frog.png','assets/maid.png','assets/voice.wav','assets/token.wav','assets/maid-swallow.png') or p.relative_to(root).as_posix().startswith('scripts/native/') or p.name in ('Start-RecyclePet.cmd','Test-RecyclePet.cmd','Runtime.ps1','Start-RecyclePet.ps1','Test-RecyclePet.ps1'))}
selected['manifest.json']=(json.dumps({'version':'0.5.0','files':[{'path':name,'sha256':hashlib.sha256(data).hexdigest()} for name,data in sorted(selected.items())]},ensure_ascii=False,indent=2)+'\n').encode('utf-8')
buffer=io.BytesIO()
with zipfile.ZipFile(buffer,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=9) as archive:
 for name,data in sorted(selected.items()):
  info=zipfile.ZipInfo('RecyclePet/'+name,date_time=(2026,11,1,0,0,0));info.compress_type=zipfile.ZIP_DEFLATED
  archive.writestr(info,data)
data=buffer.getvalue();sha=hashlib.sha256(data).hexdigest()
commands=[
 "$ErrorActionPreference='Stop'",
 "$tag='::RP_PAYLOAD::'",
 "$text=[IO.File]::ReadAllText($env:RECYCLE_PET_BUNDLE)",
 "$body=$text.Substring($text.LastIndexOf($tag)+$tag.Length).Trim()",
 "$bytes=[Convert]::FromBase64String($body)",
 "$hasher=[Security.Cryptography.SHA256]::Create()",
 "$actual=([BitConverter]::ToString($hasher.ComputeHash($bytes))).Replace('-','').ToLowerInvariant()",
 f"if($actual -ne '{sha}'){{throw 'Downloaded file is incomplete. Download it again.'}}",
 f"$dest=Join-Path $env:LOCALAPPDATA 'RecyclePetApp\\v0.5.0-{sha[:12]}'",
 "$entry=Join-Path $dest 'RecyclePet\\scripts\\Start-RecyclePet.ps1'",
 "if(-not(Test-Path -LiteralPath $entry)){[IO.Directory]::CreateDirectory($dest)|Out-Null;$zip=$dest+'.zip';[IO.File]::WriteAllBytes($zip,$bytes);Add-Type -AssemblyName System.IO.Compression.FileSystem;[IO.Compression.ZipFile]::ExtractToDirectory($zip,$dest)}",
 "$exe=Join-Path $env:WINDIR 'System32\\WindowsPowerShell\\v1.0\\powershell.exe'",
 "Start-Process -FilePath $exe -WindowStyle Hidden -ArgumentList ('-NoProfile -STA -ExecutionPolicy Bypass -File '+[char]34+$entry+[char]34)"
]
command=';'.join(commands)
header='@echo off\r\nsetlocal\r\nset "RECYCLE_PET_BUNDLE=%~f0"\r\n"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" -NoProfile -ExecutionPolicy Bypass -Command "'+command+'"\r\nif errorlevel 1 (\r\n  echo RecyclePet could not start. Please send the error above for diagnosis.\r\n  pause\r\n)\r\nexit /b\r\n::RP_PAYLOAD::\r\n'
payload='\r\n'.join(textwrap.wrap(base64.b64encode(data).decode('ascii'),120))+'\r\n'
out.write_bytes((header+payload).encode('ascii'))
# Validate exactly what the Windows launcher will decode, including every installed byte.
text=out.read_text();extracted=base64.b64decode(text[text.rindex('::RP_PAYLOAD::')+len('::RP_PAYLOAD::'):].strip())
assert hashlib.sha256(extracted).hexdigest()==sha
with zipfile.ZipFile(io.BytesIO(extracted)) as archive:
 assert archive.testzip() is None
 assert all(not x.startswith('/') and '..' not in Path(x).parts for x in archive.namelist())
 for name,data in selected.items():assert archive.read('RecyclePet/'+name)==data
 assert 'RecyclePet/Start-RecyclePet.cmd' in archive.namelist()
 assert 'RecyclePet/manifest.json' in archive.namelist()
 assert 'RecyclePet/SKILL.md' not in archive.namelist()
assert len(header.splitlines()[3])<8000
print(json.dumps({'output':str(out),'bytes':out.stat().st_size,'embedded_sha256':sha,'files':len(zipfile.ZipFile(io.BytesIO(extracted)).namelist()),'carrier_roundtrip':'PASS','windows_execution':'NOT_RUN'}))
