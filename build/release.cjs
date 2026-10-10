'use strict';
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const { createReadStream, createWriteStream } = require('node:fs');
const { Readable, Transform } = require('node:stream');
const { pipeline } = require('node:stream/promises');
const repository = 'yxsimony-dot/CCB-USB-User';
const repositoryUrl = `https://github.com/${repository}`;
const identityKeys = ['productId','version','baseVersion','releaseSequence','workflowRunId','workflowRunNumber','sourceCommit','sourceRepository'];
function version(value) {
  if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(value)) throw Error('Invalid stable version');
  const parts = value.split('.').map(Number);
  if (parts.some(p => p > 65535)) throw Error('Version exceeds Windows range');
  return parts;
}
function compare(a,b) { const x=version(a),y=version(b); for(let i=0;i<3;i++)if(x[i]!==y[i])return Math.sign(x[i]-y[i]);return 0; }
function identity(m) { return Object.fromEntries(identityKeys.map(k=>[k,m[k]])); }
function assertIdentity(actual, expected) {
  if (identityKeys.some(k=>actual[k]!==expected[k])) throw Error('Release identity conflict');
}
function validateManifest(m) {
  const base=version(m.baseVersion), effective=version(m.version);
  if(m.schemaVersion!==1 || m.productId!=='usb-web-launcher' || m.sourceRepository!==repositoryUrl || m.sourceDirty!==false ||
    m.channel!=='stable' || m.platform!=='windows' || m.architecture!=='x64' || !/^[a-f0-9]{40}$/.test(m.sourceCommit) ||
    !Number.isSafeInteger(m.releaseSequence) || m.releaseSequence<1 || m.releaseSequence>65535 || m.workflowRunNumber!==m.releaseSequence ||
    !/^[1-9]\d*$/.test(m.workflowRunId) || m.workflowRunUrl!==`${repositoryUrl}/actions/runs/${m.workflowRunId}` ||
    effective[0]!==base[0] || effective[1]!==base[1] || effective[2]!==base[2]+m.releaseSequence ||
    m.installerFileName!==`UsbWebLauncher-${m.version}-win-x64-setup.exe` || !Number.isSafeInteger(m.size) || m.size<=0 || m.size>1073741824 ||
    !/^[a-f0-9]{64}$/.test(m.sha256)) throw Error('Invalid release manifest or version mapping');
}
function assertVersionHistory(candidate, previous) {
  for(const old of previous) {
    if(candidate.workflowRunId===old.workflowRunId) { assertIdentity(candidate,old); continue; }
    if(candidate.version===old.version || candidate.releaseSequence===old.releaseSequence) throw Error('Version/sequence identity conflict');
    const later=BigInt(candidate.workflowRunId)>BigInt(old.workflowRunId);
    if(later !== (candidate.releaseSequence>old.releaseSequence)) throw Error('Workflow sequence reset or run identity conflict');
    if(later ? compare(candidate.baseVersion,old.baseVersion)<0 : compare(candidate.baseVersion,old.baseVersion)>0) throw Error('Base version rollback');
    if(later ? compare(candidate.version,old.version)<=0 : compare(candidate.version,old.version)>=0) throw Error('Effective version order conflicts with run sequence');
  }
}
function releaseIdentity(release) {
  const match=/<!-- usb-web-launcher-release:([^\r\n]+) -->/.exec(release.body || '');
  if(!match) throw Error(`Missing managed release identity: ${release.tag_name}`);
  return JSON.parse(match[1]);
}
async function fileHash(file) { const hash=crypto.createHash('sha256');for await(const chunk of createReadStream(file))hash.update(chunk);return hash.digest('hex'); }
async function verifyFiles(dir, expected) {
  const raw=await fs.readFile(path.join(dir,'release-manifest.json'));
  if(raw.length>65536) throw Error('Manifest too large');
  const m=JSON.parse(raw.toString('utf8').replace(/^\uFEFF/,'')); validateManifest(m); assertIdentity(m,expected);
  const installer=path.join(dir,m.installerFileName);
  if((await fs.stat(installer)).size!==m.size || await fileHash(installer)!==m.sha256)throw Error('Installer size or SHA-256 mismatch');
  const sums=await fs.readFile(path.join(dir,'SHA256SUMS'),'utf8');
  if(sums.trim()!==`${m.sha256}  ${m.installerFileName}`)throw Error('SHA256SUMS mismatch');
  return m;
}
async function verifyRemote(api, release, expected, localDirectory) {
  const names=[`UsbWebLauncher-${expected.version}-win-x64-setup.exe`,'release-manifest.json','SHA256SUMS'];
  if(release.assets.length!==3 || names.some(n=>release.assets.filter(a=>a.name===n&&a.state==='uploaded').length!==1))throw Error('Remote release assets missing, duplicate or unexpected');
  const temp=await fs.mkdtemp(path.join(os.tmpdir(),'uwl-remote-'));
  try {
    for(const name of names) {
      const asset=release.assets.find(a=>a.name===name);
      const limit=name.endsWith('.exe')?1073741824:65536;
      if(!Number.isSafeInteger(asset.size)||asset.size<=0||asset.size>limit)throw Error('Invalid remote asset size');
      await api.download(asset.id,path.join(temp,name),asset.size);
      if((await fs.stat(path.join(temp,name))).size!==asset.size)throw Error('Remote asset size mismatch');
      if(localDirectory && await fileHash(path.join(temp,name))!==await fileHash(path.join(localDirectory,name)))throw Error('Remote asset bytes differ from this build');
    }
    return await verifyFiles(temp,expected);
  } finally { await fs.rm(temp,{recursive:true,force:true}); }
}
async function publishRelease(dir, api, notes = '') {
  const m=JSON.parse((await fs.readFile(path.join(dir,'release-manifest.json'),'utf8')).replace(/^\uFEFF/,''));
  validateManifest(m);
  const tag=`v${m.version}`;
  const releases=await api.list();
  const histories=releases.filter(r=>/^v\d+\.\d+\.\d+$/.test(r.tag_name)&&compare(r.tag_name.slice(1),'1.1.0')>=0).map(releaseIdentity);
  assertVersionHistory(m,histories);
  const taggedSha=await api.tag(tag);
  if(taggedSha && taggedSha!==m.sourceCommit)throw Error('Git tag source conflict');
  let release=releases.find(r=>r.tag_name===tag);
  if(release) {
    assertIdentity(releaseIdentity(release),m);
    if(release.prerelease)throw Error('Release is unexpectedly prerelease');
    if(release.draft && release.target_commitish!==m.sourceCommit)throw Error('Draft target source identity conflict');
    if(!release.draft) {
      if(taggedSha!==m.sourceCommit)throw Error('Published tag missing or changed');
      await verifyRemote(api,await api.get(release.id),m);
      return {version:m.version,id:release.id,reused:true};
    }
  }
  await verifyFiles(dir,m);
  if(!release)release=await api.create({tag_name:tag,target_commitish:m.sourceCommit,name:`UsbWebLauncher ${m.version}`,draft:true,prerelease:false,
    body:`${notes ? notes + '\n\n' : ''}完整 Windows x64 安装包。自动测试通过；真实银行硬件及干净机验收需另行记录。\n\n<!-- usb-web-launcher-release:${JSON.stringify(identity(m))} -->`});
  // Only this run's draft may be replaced. Rebuild timestamps can change bytes.
  for(const asset of release.assets)await api.remove(asset.id);
  for(const name of [m.installerFileName,'SHA256SUMS','release-manifest.json'])await api.upload(release.id,name,path.join(dir,name));
  release=await api.get(release.id);
  assertIdentity(releaseIdentity(release),m);
  if(!release.draft || release.prerelease || release.target_commitish!==m.sourceCommit)throw Error('Release target identity changed during upload');
  await verifyRemote(api,release,m,dir);
  const latestTag=await api.tag(tag);
  if(latestTag && latestTag!==m.sourceCommit)throw Error('Git tag changed during upload');
  await api.publish(release.id);
  if(await api.tag(tag)!==m.sourceCommit)throw Error('Published tag source mismatch');
  return {version:m.version,id:release.id,reused:false};
}
function githubApi(token) {
  if(!token)throw Error('GH_TOKEN is required');
  const prefix=`https://api.github.com/repos/${repository}`;
  async function request(url, options={}) {
    const response=await fetch(url,{...options,redirect:'manual',signal:AbortSignal.timeout(600000),headers:{Accept:'application/vnd.github+json','X-GitHub-Api-Version':'2022-11-28','User-Agent':'UsbWebLauncher-release',Authorization:`Bearer ${token}`,...options.headers}});
    if(response.status===404 && options.allowMissing)return null;
    if(!response.ok)throw Error(`GitHub API failed: HTTP ${response.status}`);
    return response;
  }
  async function json(endpoint, method='GET', body,allowMissing=false) {
    const response=await request(prefix+endpoint,{method,allowMissing,...(body?{body:JSON.stringify(body),headers:{'Content-Type':'application/json'}}:{})});
    return response?response.json():null;
  }
  return {
    async list(){const results=[];for(let page=1;page<=1000;page++){const batch=await json(`/releases?per_page=100&page=${page}`);results.push(...batch);if(batch.length<100)return results;}throw Error('Release history pagination limit exceeded');},
    async tag(tag){const ref=await json(`/git/ref/tags/${encodeURIComponent(tag)}`,'GET',undefined,true);if(!ref)return null;let object=ref.object;for(let i=0;i<8 && object.type==='tag';i++)object=(await json(`/git/tags/${object.sha}`)).object;if(object.type!=='commit')throw Error('Invalid tag object');return object.sha;},
    create: body=>json('/releases','POST',body),
    get: id=>json(`/releases/${id}`),
    async remove(id){await request(`${prefix}/releases/assets/${id}`,{method:'DELETE'});},
    async upload(id,name,file){await request(`https://uploads.github.com/repos/${repository}/releases/${id}/assets?name=${encodeURIComponent(name)}`,{method:'POST',headers:{'Content-Type':'application/octet-stream','Content-Length':String((await fs.stat(file)).size)},body:createReadStream(file),duplex:'half'});},
    async download(id,file,limit){
      let response=await fetch(`${prefix}/releases/assets/${id}`,{redirect:'manual',signal:AbortSignal.timeout(600000),headers:{Accept:'application/octet-stream',Authorization:`Bearer ${token}`,'User-Agent':'UsbWebLauncher-release','X-GitHub-Api-Version':'2022-11-28'}});
      if([301,302,303,307,308].includes(response.status)) {
        const url=new URL(response.headers.get('location'));
        if(url.protocol!=='https:' || !['release-assets.githubusercontent.com','objects.githubusercontent.com'].includes(url.hostname))throw Error('Untrusted GitHub asset redirect');
        response=await fetch(url,{redirect:'error',signal:AbortSignal.timeout(600000)});
      }
      if(!response.ok)throw Error(`Asset download failed: HTTP ${response.status}`);
      let count=0;await pipeline(Readable.fromWeb(response.body),new Transform({transform(chunk,encoding,callback){count+=chunk.length;callback(count>limit?Error('Asset exceeds declared length'):null,chunk);}}),createWriteStream(file,{flags:'wx'}));
    },
    publish:id=>json(`/releases/${id}`,'PATCH',{draft:false,prerelease:false,make_latest:'legacy'})
  };
}
module.exports={publishRelease,assertVersionHistory,validateManifest,githubApi};
if(require.main===module) {
  if(process.env.GITHUB_REPOSITORY!==repository || process.env.GITHUB_EVENT_NAME!=='push' || process.env.GITHUB_REF!=='refs/heads/main')throw Error('Publication is restricted to the target repository main push');
  (async()=>{
    const dir=path.resolve(process.argv[2] || 'artifacts/release');
    const m=JSON.parse((await fs.readFile(path.join(dir,'release-manifest.json'),'utf8')).replace(/^\uFEFF/,''));
    if(m.sourceCommit!==process.env.GITHUB_SHA || m.workflowRunId!==process.env.GITHUB_RUN_ID || m.workflowRunNumber!==Number(process.env.GITHUB_RUN_NUMBER))throw Error('Artifact does not belong to this immutable workflow run');
    const notes=require('node:child_process').execFileSync('git',['show','-s','--format=%s',m.sourceCommit],{encoding:'utf8'}).trim();
    console.log(await publishRelease(dir,githubApi(process.env.GH_TOKEN),notes));
  })().catch(error=>{console.error(error.message);process.exitCode=1;});
}
