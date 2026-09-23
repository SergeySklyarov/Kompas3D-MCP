#!/usr/bin/env node
/** Local project dashboard. No dependencies, no CAD calls, no external network. */
import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const out = path.join(root, 'docs', 'progress');
const source = 'coverage/solid-v24/';
const inputPaths = [source+'matrix.json', source+'catalog.json', source+'release-profiles/mechanical-core-v1.json'];
const actionsLabel = {discover:'Обнаружение',create:'Создание',read:'Чтение',edit:'Изменение',rebuild:'Перестроение',save_reopen:'Повторное открытие',suppress_restore:'Подавление',delete_dependencies:'Удаление и связи',negative_tests:'Отказы',geometry_validation:'Геометрия'};
const names = {'SM-02':['Выдавливание','Основание, добавление, вырез'],'SM-03':['Вращение','Основание, бобышка, вырез'],'SM-04':['По траектории','Профиль вдоль плоской кривой'],'SM-05':['По сечениям','Переход между профилями'],'SM-07':['Отверстия','Глухие, сквозные, цековка'],'SM-09':['Скругления','Радиус и выбранные рёбра'],'SM-11':['Фаски','Катеты, расстояние и угол'],'SM-13':['Оболочка','Толщина и удаление граней'],'SM-15':['Булевы операции','Объединение, разность, пересечение'],'SM-16':['Разделение','Плоскость и сторона отсечения'],'SM-17':['Перемещение','Перенос и поворот тела'],'SM-18':['Линейный массив','Ряды и прямоугольная сетка'],'SM-19':['Круговой массив','Ось, угол и экземпляры'],'SM-23':['Зеркало','Отражение операций и тел']};
const read = rel => JSON.parse(fs.readFileSync(path.join(root,rel),'utf8').replace(/^\uFEFF/,''));
const arrays = v => Array.isArray(v) ? v : v == null ? [] : [typeof v === 'string' ? v : JSON.stringify(v)];
function state(values) {
  if (values.length && values.every(s => ['verified','not_applicable'].includes(s))) return 'done';
  if (values.some(s => s.startsWith('blocked_'))) return 'blocked';
  if (values.some(s => !['not_started','not_applicable'].includes(s))) return 'partial';
  return 'pending';
}
function build() {
  const [matrix,catalog,profile] = inputPaths.map(read);
  const actions = matrix.meta.actions;
  const byId = new Map();
  for (const row of matrix.rows) {
    if (byId.has(row.operation_id)) throw Error('Дублирующийся ID: '+row.operation_id);
    byId.set(row.operation_id,row);
  }
  const allowed = new Set(matrix.meta.statuses);
  function item(entry, dependency=false) {
    const id = dependency ? entry.id : entry.ref;
    const row = byId.get(id);
    const required = dependency ? (entry.required_actions?.length ? entry.required_actions : actions) : actions;
    const checks = required.map(key => {
      const status = row?.actions?.[key] ?? 'not_started';
      if (!allowed.has(status)) throw Error('Неизвестный статус '+id+': '+status);
      if (status === 'not_applicable' && !row?.not_applicable_reasons?.[key]) throw Error('Нет обоснования not_applicable: '+id+'/'+key);
      return {key,label:actionsLabel[key]||key,status};
    });
    if (checks.some(c=>c.status==='verified') && !(row?.tests?.length || row?.evidence?.length)) throw Error('Нет доказательства: '+id);
    return {id,title:entry.title||id,queue:entry.queue,state:state(checks.map(c=>c.status)),checks,
      tests:arrays(row?.tests),evidence:arrays(row?.evidence),limits:arrays(row?.limitations),
      gap:entry.known_gap||entry.gap||entry.blocking||null};
  }
  const required = profile.modes.filter(m=>m.priority==='practical_required');
  const modes = required.map(m=>item(m));
  const dependencies = profile.common_dependencies.map(d=>item(d,true));
  const closed = list=>list.filter(x=>x.state==='done').length;
  const groups = [...new Set(required.map(m=>m.family))].map(id=>{
    const entries = modes.filter((_,i)=>required[i].family===id);
    const known = catalog.families.find(f=>f.id===id);
    return {id,title:names[id]?.[0]||known?.title||id,description:names[id]?.[1]||known?.title||'',queue:entries[0].queue,
      modes:entries,closed:closed(entries),total:entries.length,state:state(entries.map(e=>e.state==='done'?'verified':e.state==='blocked'?'blocked_api':e.state==='partial'?'implemented':'not_started'))};
  }).sort((a,b)=>a.queue.localeCompare(b.queue)||a.id.localeCompare(b.id));
  const queues = [...new Set(modes.map(m=>m.queue))].sort().map(id=>{
    const entries=modes.filter(m=>m.queue===id);
    return {id,total:entries.length,closed:closed(entries),started:entries.some(e=>['partial','blocked'].includes(e.state)),blocked:entries.some(e=>e.state==='blocked')};
  });
  const passports = fs.readdirSync(path.join(root,'docs/acceptance'),{withFileTypes:true}).filter(d=>d.isDirectory())
    .map(d=>'docs/acceptance/'+d.name+'/delivery-passport.json').filter(p=>fs.existsSync(path.join(root,p)));
  passports.sort((a,b)=>fs.statSync(path.join(root,b)).mtimeMs-fs.statSync(path.join(root,a)).mtimeMs);
  let delivery=null;
  if(passports.length){
    const p=read(passports[0]);
    delivery={source:passports[0],modified:fs.statSync(path.join(root,passports[0])).mtime.toISOString(),
      client:p.client_acceptance?.client||p.client_acceptance?.client_name||'Рабочий клиент',
      verdict:p.client_acceptance?.verdict||p.client_acceptance?.status||'not_run',
      rows:p.acceptance?.rows??null,failed:p.acceptance?.failed??null,
      packageVerdict:p.package?.verdict||'unknown',matches:p.client_acceptance?.delivery_matches_package??null};
  }
  const files=[...inputPaths,...passports.slice(0,1)];
  const updated=new Date(Math.max(...files.map(p=>fs.statSync(path.join(root,p)).mtimeMs))).toISOString();
  const done=closed(modes)+closed(dependencies),total=modes.length+dependencies.length;
  const data={schemaVersion:1,generatedAt:new Date().toISOString(),updatedAt:updated,profile:profile.meta.title,
    build:matrix.meta.target_build,modeClosed:closed(modes),modeTotal:modes.length,dependencyClosed:closed(dependencies),dependencyTotal:dependencies.length,
    percent:total?Math.round(done/total*1000)/10:0,done,total,groups,queues,dependencies,delivery,sources:files};
  const dataText=JSON.stringify(data).replace(/</g,'\\u003c');
  const template=fs.readFileSync(path.join(out,'dashboard.template.html'),'utf8');
  if(!template.includes('/*__PROJECT_DATA__*/')) throw Error('Шаблон не содержит точки вставки данных');
  const html=template.replace('/*__PROJECT_DATA__*/',dataText);
  for(const [name,content] of [['data.json',JSON.stringify(data,null,2)+'\n'],['index.html',html]]){
    const target=path.join(out,name), temp=target+'.tmp';
    fs.writeFileSync(temp,content,'utf8');fs.renameSync(temp,target);
  }
  console.log(`Готовность ${data.percent}% · режимы ${data.modeClosed}/${data.modeTotal} · зависимости ${data.dependencyClosed}/${data.dependencyTotal}`);
  return data;
}

build();
if(process.argv.includes('--serve')){
  const pi=process.argv.indexOf('--port');
  const port=pi<0?8766:Number(process.argv[pi+1]);
  if(!Number.isInteger(port)||port<1024||port>65535) throw Error('Порт должен быть от 1024 до 65535');
  let refreshError=null;
  const server=http.createServer((req,res)=>{
    const url=new URL(req.url,'http://127.0.0.1');
    const file=url.pathname==='/data.json'?'data.json':url.pathname==='/'||url.pathname==='/index.html'?'index.html':null;
    if(!file){res.writeHead(404);return res.end('Not found');}
    if(file==='data.json'&&refreshError){res.writeHead(503,{'Content-Type':'application/json; charset=utf-8','Cache-Control':'no-store'});return res.end(JSON.stringify({error:refreshError}));}
    res.writeHead(200,{'Content-Type':file.endsWith('.json')?'application/json; charset=utf-8':'text/html; charset=utf-8','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});
    res.end(fs.readFileSync(path.join(out,file)));
  });
  server.on('error',e=>{console.error(e.code==='EADDRINUSE'?`Порт ${port} занят. Выберите другой: --port 8767`:e.message);process.exit(1);});
  server.listen(port,'127.0.0.1',()=>console.log(`Страница: http://127.0.0.1:${port} · автоматическое обновление · Ctrl+C — остановить`));
  function stamp(){
    const roots=[path.join(root,'coverage/solid-v24'),path.join(root,'docs/acceptance')];
    const collect=dir=>fs.readdirSync(dir,{withFileTypes:true}).flatMap(d=>d.isDirectory()?(d.name==='archive'?[]:collect(path.join(dir,d.name))):d.name.endsWith('.json')?[path.join(dir,d.name)]:[]);
    return roots.flatMap(collect).concat(path.join(out,'dashboard.template.html')).map(p=>p+':'+fs.statSync(p).mtimeMs).join('|');
  }
  let last=stamp();
  setInterval(()=>{try{const next=stamp();if(next!==last){build();last=next;refreshError=null;}}catch(e){refreshError=e.message;console.error('Обновление отложено: '+e.message);}},2000);
}
