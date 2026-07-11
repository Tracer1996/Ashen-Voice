const token = document.getElementById('token');
const guildId = document.getElementById('guildId');
const channelId = document.getElementById('channelId');
const start = document.getElementById('start');
const stop = document.getElementById('stop');
const statusText = document.getElementById('status');
const statusCard = document.querySelector('.status-card');

function settings(){return {token:token.value.trim(),guildId:guildId.value.trim(),channelId:channelId.value.trim()}}
function setStatus(message,state='idle'){statusText.textContent=message;statusCard.className='status-card '+state}

window.overlay.loadSettings().then(s=>{token.value=s.token||'';guildId.value=s.guildId||'';channelId.value=s.channelId||''});
window.overlay.onStatus(({message,state})=>setStatus(message,state));
start.addEventListener('click',async()=>{start.disabled=true;setStatus('Starting...','working');try{await window.overlay.start(settings())}catch(e){setStatus(e.message,'error')}finally{start.disabled=false}});
stop.addEventListener('click',async()=>{await window.overlay.stop();setStatus('Overlay stopped.','idle')});
