// The actual UIE engine and visible Home cards. Unvisited feature cards are excluded
// from this acceptance's source graph; their product behavior is not asserted.
export * from '../../../apps/TinadecUI/src/engine/index.ts';
export * from '../../../apps/TinadecUI/src/components/useUie.ts';
export { default as UieShell } from '../../../apps/TinadecUI/src/components/UieShell.vue';
export { default as UieCanvas } from '../../../apps/TinadecUI/src/components/UieCanvas.vue';
export { default as UieColumn } from '../../../apps/TinadecUI/src/components/UieColumn.vue';
export { default as UieStack } from '../../../apps/TinadecUI/src/components/UieStack.vue';
export { default as UieDock } from '../../../apps/TinadecUI/src/components/UieDock.vue';
export { default as UieCardHost } from '../../../apps/TinadecUI/src/components/UieCardHost.vue';
export { default as UieCardFrame } from '../../../apps/TinadecUI/src/components/UieCardFrame.vue';
import { createCardRegistry } from '../../../apps/TinadecUI/src/engine/registry.ts';
import ChatCard from '../../../apps/TinadecUI/src/components/cards/home/ChatCard.vue';
import TerminalCard from '../../../apps/TinadecUI/src/components/cards/home/TerminalCard.vue';
import NavCard from '../../../apps/TinadecUI/src/components/cards/home/NavCard.vue';
import HomePickerCard from '../../../apps/TinadecUI/src/components/cards/home/HomePickerCard.vue';
const unused={render:()=>null};
export function buildUieRegistry(){
  const r=createCardRegistry();
  for(const [type,minWidth,minHeight,title,fixed] of [['nav',220,120,'项目',true],['chat',320,200,'聊天',true],['homePicker',240,160,'Home',true],['terminal',260,160,'终端',false]])
    r.register({type,component:{nav:NavCard,chat:ChatCard,homePicker:HomePickerCard,terminal:TerminalCard}[type],minWidth,minHeight,singleton:true,movable:!fixed,closable:!fixed,detachable:!fixed,defaultTitle:title});
  for(const type of ['git','approval','orchestration','organization','events','doctor','browser','agent','spatialWork','marketFilter','marketCatalog','marketDetail'])
    r.register({type,component:unused,minWidth:260,minHeight:160,singleton:true,movable:true,closable:true,detachable:true,defaultTitle:type});
  return r;
}
export function createComponentLookup(registry){return id=>registry.get(id)?.component;}
