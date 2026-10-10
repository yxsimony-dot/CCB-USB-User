const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { publishRelease, assertVersionHistory } = require('../../build/release.cjs');
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const sha = 'a'.repeat(40);
function manifest(n = 10, run = 100) {
  const installer = Buffer.from('synthetic installer');
  return {schemaVersion:1,productId:'usb-web-launcher',version:`1.1.${n}`,baseVersion:'1.1.0',releaseSequence:n,
    workflowRunId:String(run),workflowRunNumber:n,workflowRunUrl:`https://github.com/yxsimony-dot/CCB-USB-User/actions/runs/${run}`,
    sourceCommit:sha,sourceDirty:false,sourceRepository:'https://github.com/yxsimony-dot/CCB-USB-User',channel:'stable',
    platform:'windows',architecture:'x64',installerFileName:`UsbWebLauncher-1.1.${n}-win-x64-setup.exe`,size:installer.length,sha256:hash(installer)};
}
async function fixture(t, m = manifest()) {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(),'uwl-release-'));
  t.after(()=>fs.rm(dir,{recursive:true,force:true}));
  await fs.writeFile(path.join(dir,m.installerFileName),'synthetic installer');
  await fs.writeFile(path.join(dir,'release-manifest.json'),JSON.stringify(m));
  await fs.writeFile(path.join(dir,'SHA256SUMS'),`${m.sha256}  ${m.installerFileName}\n`);
  let serial = 1;
  const api = {
    releases:[], tags:new Map(), data:new Map(), uploads:0, failUpload:0, corrupt:false, drop:false,
    async list(){return structuredClone(this.releases);},
    async tag(tag){return this.tags.get(tag) ?? null;},
    async create(body){const r={...body,id:serial++,assets:[]};this.releases.push(r);return structuredClone(r);},
    async get(id){return structuredClone(this.releases.find(r=>r.id===id));},
    async upload(id,name,file){this.uploads++;if(this.uploads===this.failUpload)throw Error('interrupted');const bytes=await fs.readFile(file);const asset={id:serial++,name,size:bytes.length,state:'uploaded'};if(!this.drop)this.releases.find(r=>r.id===id).assets.push(asset);this.data.set(asset.id,bytes);},
    async remove(id){for(const r of this.releases)r.assets=r.assets.filter(a=>a.id!==id);this.data.delete(id);},
    async download(id,file){const bytes=this.corrupt?Buffer.from('corruption'):this.data.get(id);await fs.writeFile(file,bytes);},
    async publish(id){const r=this.releases.find(r=>r.id===id);r.draft=false;this.tags.set(r.tag_name,r.target_commitish);return structuredClone(r);}
  };
  return {dir,api,m};
}
test('publishes only complete remotely verified assets',async t=>{const {dir,api}=await fixture(t);await publishRelease(dir,api);assert.equal(api.releases[0].draft,false);assert.equal(api.releases[0].target_commitish,sha);assert.deepEqual(api.releases[0].assets.map(a=>a.name).sort(),['SHA256SUMS','UsbWebLauncher-1.1.10-win-x64-setup.exe','release-manifest.json']);});
test('upload failure remains draft and same run resumes',async t=>{const {dir,api}=await fixture(t);api.failUpload=2;await assert.rejects(publishRelease(dir,api),/interrupted/);assert.equal(api.releases[0].draft,true);api.failUpload=0;await publishRelease(dir,api);assert.equal(api.releases.length,1);assert.equal(api.releases[0].draft,false);});
for(const mode of ['corrupt','drop'])test(`${mode} remote assets prevent publication`,async t=>{const {dir,api}=await fixture(t);api[mode]=true;await assert.rejects(publishRelease(dir,api));assert.equal(api.releases[0].draft,true);});
test('published rerun validates remote original and never replaces rebuilt bytes',async t=>{const {dir,api,m}=await fixture(t);await publishRelease(dir,api);const uploads=api.uploads;const changed=Buffer.from('different rebuilt installer');m.size=changed.length;m.sha256=hash(changed);await fs.writeFile(path.join(dir,m.installerFileName),changed);await fs.writeFile(path.join(dir,'release-manifest.json'),JSON.stringify(m));await fs.writeFile(path.join(dir,'SHA256SUMS'),`${m.sha256}  ${m.installerFileName}\n`);await publishRelease(dir,api);assert.equal(api.uploads,uploads);api.corrupt=true;await assert.rejects(publishRelease(dir,api));assert.equal(api.uploads,uploads);});
test('tag and release run identity conflicts cannot overwrite',async t=>{const {dir,api,m}=await fixture(t);api.tags.set('v1.1.10','b'.repeat(40));await assert.rejects(publishRelease(dir,api),/tag/i);assert.equal(api.uploads,0);api.tags.clear();await publishRelease(dir,api);m.workflowRunId='999';m.workflowRunUrl='https://github.com/yxsimony-dot/CCB-USB-User/actions/runs/999';await fs.writeFile(path.join(dir,'release-manifest.json'),JSON.stringify(m));await assert.rejects(publishRelease(dir,api),/identity|conflict/i);assert.equal(api.uploads,3);});
test('older run can complete after higher run without relabelling',()=>{assert.doesNotThrow(()=>assertVersionHistory(manifest(9,99),[manifest(10,100)]));assert.doesNotThrow(()=>assertVersionHistory(manifest(11,101),[manifest(10,100)]));});
test('new run after counter reset is rejected',()=>{assert.throws(()=>assertVersionHistory(manifest(1,101),[manifest(10,100)]),/reset|sequence/i);});
test('base rollback and nonincreasing mapped versions rejected',()=>{const old=manifest(10,100);assert.throws(()=>assertVersionHistory({...manifest(11,101),baseVersion:'1.0.0',version:'1.0.11'},[old]),/base|version/i);assert.throws(()=>assertVersionHistory({...manifest(11,101),version:'1.1.10'},[old]),/version/i);});
test('draft verification compares remote bytes to this build even if remote metadata is coherent',async t=>{
  const {dir,api,m}=await fixture(t);
  const originalGet=api.get.bind(api);
  api.get=async id=>{
    const r=await originalGet(id);
    const replacement=Buffer.from('coherently replaced installer');
    const alternate={...m,size:replacement.length,sha256:hash(replacement)};
    for(const a of api.releases[0].assets) {
      const bytes=a.name.endsWith('.exe')?replacement:a.name==='release-manifest.json'?Buffer.from(JSON.stringify(alternate)):Buffer.from(`${alternate.sha256}  ${alternate.installerFileName}\n`);
      api.data.set(a.id,bytes);a.size=bytes.length;
    }
    return await originalGet(id);
  };
  await assert.rejects(publishRelease(dir,api),/this build|bytes/i);
  assert.equal(api.releases[0].draft,true);
});
test('draft target SHA cannot differ from claimed identity',async t=>{
  const {dir,api}=await fixture(t);api.failUpload=1;
  await assert.rejects(publishRelease(dir,api),/interrupted/);
  api.releases[0].target_commitish='b'.repeat(40);api.failUpload=0;
  await assert.rejects(publishRelease(dir,api),/target|source|identity/i);
  assert.equal(api.releases[0].draft,true);
});
test('two pushes publish out of order while preserving each version and source identity',async t=>{
  const newer=await fixture(t,manifest(10,100));
  const olderManifest={...manifest(9,99),sourceCommit:'b'.repeat(40)};
  const older=await fixture(t,olderManifest);
  await publishRelease(newer.dir,newer.api);
  await publishRelease(older.dir,newer.api);
  assert.deepEqual(newer.api.releases.map(r=>[r.tag_name,r.target_commitish,r.draft]),[['v1.1.10',sha,false],['v1.1.9','b'.repeat(40),false]]);
  const reset=await fixture(t,manifest(1,101));
  await assert.rejects(publishRelease(reset.dir,newer.api),/reset|sequence/i);
  assert.equal(newer.api.releases.length,2);
});
test('installer size boundary matches client 1 GiB limit without allocating payload bytes',()=>{
  const {validateManifest}=require('../../build/release.cjs');
  assert.doesNotThrow(()=>validateManifest({...manifest(),size:1073741824}));
  assert.throws(()=>validateManifest({...manifest(),size:1073741825}),/manifest/i);
});
test('remote asset larger than client limit is rejected before downloading',async t=>{
  const {dir,api}=await fixture(t);
  const originalGet=api.get.bind(api);
  api.get=async id=>{const release=await originalGet(id);release.assets.find(a=>a.name.endsWith('.exe')).size=1073741825;return release;};
  api.download=async ()=>{throw Error('oversized asset reached download');};
  await assert.rejects(publishRelease(dir,api),/Invalid remote asset size/);
  assert.equal(api.releases[0].draft,true);
});
