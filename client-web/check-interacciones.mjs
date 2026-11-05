// Regresiones del cliente real: bootstrap del libro, atajos, cancelación y muerte.
// Se ejecutan sus funciones y listeners con el codec real y un DOM mínimo; no se copia la lógica.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { ByteQueueWriter, xorBuffer } from './dist/src/codec/bytequeue.js';
import { parseLocaleMsg } from './dist/src/protocol/localeSmg.js';

const html = readFileSync(process.argv[2] || new URL('./index.html', import.meta.url), 'utf8');
const script = html.match(/<script type="module">([\s\S]*?)<\/script>/)[1];
const ast = ts.createSourceFile('index.js', script, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
const functions = new Map(ast.statements.filter(ts.isFunctionDeclaration).map(n => [n.name.text, n.getText(ast)]));
function declaration(name) {
  for (const s of ast.statements.filter(ts.isVariableStatement))
    for (const d of s.declarationList.declarations)
      if (d.name.getText(ast) === name) return d.initializer.getText(ast);
  throw new Error(`Falta la declaración ${name}`);
}
function listener(target, event) {
  const call = ast.statements.filter(ts.isExpressionStatement).map(n => n.expression).find(n =>
    ts.isCallExpression(n) && n.expression.getText(ast) === target && n.arguments[0]?.text === event);
  assert.ok(call, `Falta ${target}(${event})`);
  return call.arguments[1].getText(ast);
}
function element() {
  let children = [];
  return {
    dataset: {}, style: {}, disabled: false, hidden: true, textContent: '',
    classList: { values: new Set(), add(c) { this.values.add(c); }, contains(c) { return this.values.has(c); } },
    set innerHTML(v) { children = []; }, get children() { return children; },
    appendChild(c) { children.push(c); },
  };
}
const nodes = new Map();
const $ = id => { if (!nodes.has(id)) nodes.set(id, element()); return nodes.get(id); };
const sent = [], messages = [];
const ctx = vm.createContext({
  $, document: { createElement: element, activeElement: null }, ByteQueueWriter, xorBuffer,
  ws: { readyState: 1, send: bytes => sent.push([...bytes]) }, myKey: 0,
  send: writer => sent.push([...writer.toBytes()]), W: () => new ByteQueueWriter(),
  spells: {}, inventory: {}, hotbar: [], spellListEl: $('spelllist'), invgridEl: $('invgrid'),
  dpr: 1, items: null, slotSel: 0, bankOpen: false, fichaDeObjeto: () => '',
  esc: s => s, spellName: id => `Conjuro ${id}`,
  log: text => messages.push(text), buildHotbar() {},
  canvas: $('view'), loggedIn: true, capturandoTecla: null, chatbox: $('chatbox'), modalEl: $('modal'),
  modalCard: { querySelector: selector => $(selector.slice(1)) }, modalKind: null,
  parseLocaleMsg, spellSelfText: () => '', itemName: id => `Objeto ${id}`, npcsIdx: null, SKILL_NAMES: [],
  FUENTES: {}, PISTAS_DE_RECHAZO: {},
  accionDe: key => key === 'f' ? 'lanzar' : null, HEADINGS: {},
  closeModal() { $('modal').classList.values.delete('on'); },
  tileDelEvento: () => ({ x: 30, y: 40 }), describirObjetoDelPiso() {},
  chars: new Map([[1, { name: 'Aprendiz', body: 1 }], [2, { name: 'Otra', body: 1 }]]), myCharIndex: 1,
  recentPackets: [], statusEl: $('status'), descansando: false, paralizado: false, montando: false,
  navegando: false, estupido: false, laborando: false, enCombate: false, correoPendiente: false,
});
const run = code => vm.runInContext(code, ctx);
run(`const CLIENT = ${declaration('CLIENT')}; let selectedSpell = ${declaration('selectedSpell')};
  let workSkill = 0, comercioAviso = '', slotDescripcion = 0; const SKILL_MAGIA_CLIENTE = 8, SKILL_PROYECTILES = 6;
  const CUERPO_MUERTO = ${declaration('CUERPO_MUERTO')};`);
for (const name of ['buildSpellList', 'lanzarHechizo', 'fireHotbar', 'modoPunteria', 'salirDePunteria', 'apply', 'pintarEstados', 'estoyMuerto', 'avisarEnComercio', 'buildInv', 'pintarDescripcionItem'])
  if (functions.has(name)) run(functions.get(name));
run(`const keydown = ${listener('addEventListener', 'keydown')};
  const worldClick = ${listener('canvas.addEventListener', 'click')};
  const castClick = ${listener("$('btn-cast').addEventListener", 'click')};
  const inventoryHover = ${listener('invgridEl.addEventListener', 'mouseover')};`);
const selected = () => $('spelllist').children.filter(n => n.classList.contains('sel')).map(n => n.dataset.slot);
const packet = (slot, spell) => run(`apply({name:'ChangeSpellSlot',fields:{slot:${slot},spell:${spell}}})`);
const key = value => { ctx.event = { key: value, preventDefault() {} }; run('keydown(event)'); };

packet(1, 6); // El mago recién creado sólo conoce el conjuro de la primera ranura.
assert.deepEqual(selected(), [1], 'El primer conjuro debe quedar seleccionado al entrar');
run('castClick()');
assert.deepEqual(sent.pop(), [run('CLIENT.CastSpell'), 1]);
key('f');
assert.deepEqual(sent.pop(), [run('CLIENT.CastSpell'), 1], 'F y Lanzar deben usar la misma selección');
packet(3, 3);
ctx.hotbar.push({kind: 'spell', id: 3});
run('fireHotbar(0)');
assert.deepEqual(sent.pop(), [run('CLIENT.CastSpell'), 3]);
assert.deepEqual(selected(), [3], 'La lista debe mostrar el conjuro elegido por la hotbar');
packet(3, 0);
assert.deepEqual(selected(), [1], 'Si desaparece la ranura elegida debe quedar un conjuro válido');
packet(1, 0);
assert.equal($('btn-cast').disabled, true);
const before = sent.length;
key('f'); run('castClick()');
assert.equal(sent.length, before, 'Un libro vacío no debe enviar CastSpell de una ranura inexistente');

for (const skill of [8, 6, 20]) {
  run(`modoPunteria(${skill})`);
  key('Escape');
  assert.equal($('view').style.cursor, '');
  const start = sent.length;
  run('worldClick({})');
  assert.deepEqual(sent.slice(start).map(p => p[0]), [run('CLIENT.LeftClick'), run('CLIENT.DoubleClick')],
    'Después de Escape el clic debe interactuar, sin lanzar ni trabajar');
}
// Escape en un formulario conserva la prioridad del formulario.
run('modoPunteria(8)'); $('modal').classList.add('on'); key('Escape');
assert.equal($('modal').classList.contains('on'), false);
assert.equal(run('workSkill'), 8);
key('Escape'); assert.equal(run('workSkill'), 0);

run('modoPunteria(8)');
run('apply({name:"CharacterChange",fields:{charIndex:2,body:CUERPO_MUERTO}})');
assert.equal(run('workSkill'), 8, 'La muerte ajena no cancela mi apuntado');
run('apply({name:"CharacterChange",fields:{charIndex:1,body:CUERPO_MUERTO}})');
assert.equal(run('workSkill'), 0);
assert.equal($('ayuda-muerte').hidden, false);
assert.match($('status').textContent, /muerto/);
const logs = messages.length;
run('apply({name:"CharacterChange",fields:{charIndex:1,body:21}})');
assert.equal($('ayuda-muerte').hidden, true);
assert.doesNotMatch($('status').textContent, /muerto/);
assert.equal(messages.length, logs + 1);
assert.match(messages.at(-1), /ropa.*energía/);
run('apply({name:"CharacterChange",fields:{charIndex:1,body:1}})');
assert.equal(messages.length, logs + 1, 'Cambiar equipo estando vivo no repite el aviso de resurrección');
ctx.modalKind = 'commerce';
for (const [id, text] of [[26, 'Oro insuficiente.'], [148, 'El NPC no esta interesado en comprar ese objeto.'], [25, 'No podés tener mas objetos.']]) {
  run(`apply({name:'LocaleMsg',fields:{msgId:${id},args:'',mode:0,font:0}})`);
  assert.equal($('comercio-aviso').hidden, false);
  assert.equal($('comercio-aviso').textContent, text, 'El rechazo debe ser visible en la tienda, además del chat');
}
const aviso = $('comercio-aviso').textContent;
run("apply({name:'LocaleMsg',fields:{msgId:1,args:'10',mode:2,font:2}})");
assert.equal($('comercio-aviso').textContent, aviso, 'El daño de combate no debe tapar el rechazo de la compra');
run("avisarEnComercio('')");
assert.equal($('comercio-aviso').hidden, true);
ctx.modalKind = 'bank';
run("apply({name:'LocaleMsg',fields:{msgId:26,args:'',mode:0,font:0}})");
assert.equal($('comercio-aviso').hidden, true, 'Un mensaje posterior no debe reabrir una tienda cerrada');
ctx.modalKind = null;
run("apply({name:'ChangeInventorySlot',fields:{slot:4,objIndex:563,amount:1,equipped:true}})");
ctx.hover = { target: { closest: () => ({ dataset: { slot: 4 } }) } };
run('inventoryHover(hover)');
assert.match($('itemdesc').textContent, /equipado/);
run("apply({name:'ChangeInventorySlotUser',fields:{slot:4,accion:2}})");
assert.doesNotMatch($('itemdesc').textContent, /equipado/, 'Desequipar al morir debe refrescar la ficha sin mover el mouse');
run("apply({name:'ChangeInventorySlot',fields:{slot:4,objIndex:461,amount:2,equipped:false}})");
assert.equal($('itemdesc').textContent, 'Objeto 461 (x2)', 'La ficha debe seguir el objeto actual de la ranura');
run("apply({name:'ChangeInventorySlotUser',fields:{slot:4,accion:3,valor:1}})");
assert.equal($('itemdesc').textContent, 'Objeto 461');
run("apply({name:'ChangeInventorySlotUser',fields:{slot:4,accion:3,valor:0}})");
assert.equal($('itemdesc').textContent, '—', 'Consumir o quitar el último objeto no deja una ficha vieja');
console.log('check-interacciones: OK — libro inicial/vacío, hotbar, Escape, clic posterior, muerte, resurrección, rechazos de comercio y ficha del inventario');
