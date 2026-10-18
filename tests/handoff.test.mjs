import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
let count=0;const check=(x,label)=>{assert.ok(x,label);console.log('PASS '+label);count++};
const source=fs.readFileSync(new URL('../Web/seerr-handoff.js',import.meta.url),'utf8');
const events={},docEvents={},sent=[],calls=[];let active='synthetic-user',frame,redirect;
const child={postMessage:(...x)=>sent.push(x)};
function element(tag){return {tag,dataset:{},style:{},setAttribute(){},append(){},remove(){this.removed=true},...(tag==='iframe'?{contentWindow:child}:{})};}
const window={ApiClient:{getCurrentUserId:()=>active,accessToken:()=> 'synthetic-never-export-this',getUrl:(p,q)=>p+'?code='+q.code,ajax:async x=>calls.push(x)},addEventListener:(n,f)=>events[n]=f};
const context={window,document:{currentScript:{src:'https://jellyfin.example.invalid/WizarrRequests/seerr-handoff.js'},createElement:element,body:{append:(notice,f)=>frame=f},addEventListener:(n,f)=>docEvents[n]=f},location:{assign:url=>redirect=url},fetch:async()=>({ok:true,json:async()=>({enabled:true,url:'https://seerr.example.invalid'})}),URL,URLSearchParams,crypto:globalThis.crypto,Uint8Array,setTimeout:()=>1,clearTimeout(){}};
vm.runInNewContext(source,context);await new Promise(r=>setImmediate(r));
function click(url,extra={}){let prevented=false;docEvents.click({target:{closest:()=>({href:url,dataset:{}})},button:0,preventDefault(){prevented=true},stopImmediatePropagation(){},...extra});return prevented;}
check(!click('https://other.example.invalid/movie/1'),'other destinations unchanged');
check(!click('https://seerr.example.invalid/movie/42',{ctrlKey:true}),'explicit new-tab modifier remains native');
check(click('https://seerr.example.invalid/movie/42?x=1')&&frame.hidden,'ordinary click signs in through hidden frame');
check(!frame.src.includes('synthetic-never-export-this')&&!frame.src.includes('token'),'no Jellyfin token in handoff URL');
const nonce=new URLSearchParams(new URL(frame.src).hash.slice(1)).get('nonce');
const message={type:'wizarr-seerr-quickconnect',nonce,code:'123456'};
await events.message({origin:'https://evil.example.invalid',source:child,data:message});
await events.message({origin:'https://seerr.example.invalid',source:{},data:message});
await events.message({origin:'https://seerr.example.invalid',source:child,data:{...message,nonce:'forged'}});
await events.message({origin:'https://seerr.example.invalid',source:child,data:{...message,type:'wizarr-seerr-complete'}});
check(calls.length===0&&!redirect,'forged messages and premature completion fail closed');
await events.message({origin:'https://seerr.example.invalid',source:child,data:message});
await events.message({origin:'https://seerr.example.invalid',source:child,data:message});
check(calls.length===1&&sent[0][0].type==='wizarr-seerr-authorized','native authorization occurs once');
await events.message({origin:'https://seerr.example.invalid',source:child,data:{...message,type:'wizarr-seerr-complete'}});
check(redirect==='https://seerr.example.invalid/movie/42?x=1'&&frame.removed,'successful sign-in navigates current tab and removes frame');
redirect=null;click('https://seerr.example.invalid/tv/43');const nonce2=new URLSearchParams(new URL(frame.src).hash.slice(1)).get('nonce');active='different-user';
await events.message({origin:'https://seerr.example.invalid',source:child,data:{...message,nonce:nonce2}});
check(calls.length===1&&!redirect&&frame.removed,'profile switch cancels handoff without navigation');
const receiver=fs.readFileSync(new URL('../Companion/seerr-handoff/handoff.js',import.meta.url),'utf8');
function receiverContext(destination='/movie/42?x=1',embedded=false){
 const state={calls:[],redirect:null,listener:null,status:{},manual:{hidden:true},messages:[]};const opener={postMessage:data=>state.messages.push(data)};
 const window={opener,WizarrJellyfinOrigin:'https://jellyfin.example.invalid',addEventListener:(_,f)=>state.listener=f};window.parent=embedded?opener:window;
 const c={window,document:{getElementById:id=>id==='status'?state.status:state.manual,cookie:''},location:{hash:'#'+new URLSearchParams({nonce:'a'.repeat(48),return:destination}),origin:'https://seerr.example.invalid',pathname:'/jellyfin-handoff/index.html',replace:url=>state.redirect=url},history:{replaceState(){}},URL,URLSearchParams,setTimeout:()=>1,clearTimeout(){},fetch:async (url,opts)=>{state.calls.push({url,body:JSON.parse(opts.body)});return {ok:true,json:async()=>({code:'123456',secret:'a1'.repeat(32)})}}};vm.runInNewContext(receiver,c);return {state,opener};
}
const invalid=receiverContext('//evil.example.invalid/');check(invalid.state.calls.length===0&&!invalid.state.manual.hidden,'off-origin return rejected');
const valid=receiverContext('/movie/42?x=1',true);await new Promise(r=>setImmediate(r));
await valid.state.listener({origin:'https://evil.example.invalid',source:valid.opener,data:{type:'wizarr-seerr-authorized',nonce:'a'.repeat(48)}});
check(valid.state.calls.length===1,'frame rejects wrong authorizing origin');
await valid.state.listener({origin:'https://jellyfin.example.invalid',source:valid.opener,data:{type:'wizarr-seerr-authorized',nonce:'a'.repeat(48)}});
check(valid.state.calls.length===2&&valid.state.calls[1].url.endsWith('/quickconnect/authenticate')&&valid.state.messages.at(-1).type==='wizarr-seerr-complete'&&!valid.state.redirect,'embedded receiver authenticates natively then tells source to navigate');
const handlers={},navigations=[];const reverse=fs.readFileSync(new URL('../Companion/seerr-handoff/same-tab.js',import.meta.url),'utf8');
vm.runInNewContext(reverse,{window:{WizarrJellyfinOrigin:'https://jellyfin.example.invalid',addEventListener:(n,f)=>handlers[n]=f},URL,location:{href:'https://seerr.example.invalid/movie/42',assign:x=>navigations.push(x)}});
function reverseClick(href,extra={}){handlers.click({target:{closest:()=>({href})},button:0,preventDefault(){},stopImmediatePropagation(){},...extra});}
reverseClick('https://jellyfin.example.invalid/web/#/details?id=42');reverseClick('https://elsewhere.example.invalid/');reverseClick('https://jellyfin.example.invalid/web/',{metaKey:true});
check(navigations.length===1&&navigations[0].includes('details?id=42'),'Play on Jellyfin uses current tab, unrelated and modifier links stay native');
console.log(`${count} browser handoff checks passed.`);
