// One animation owner per existing DOM surface. Retarget from the actually presented
// rectangle, never from a stale logical start and never from a screenshot/clone.
export function createSurfaceMotion(canvas, reducedMotion, getScale = () => 1) {
  const active = new Map();
  const box = node => {
    const r = node.getBoundingClientRect(), c = canvas.getBoundingClientRect(), scale = getScale();
    // DOM rectangles include page zoom; animation frames use logical CSS pixels.
    return {x:(r.left-c.left)/scale, y:(r.top-c.top)/scale, width:r.width/scale, height:r.height/scale,
      opacity:Number(getComputedStyle(node).opacity), radius:getComputedStyle(node).borderRadius};
  };
  function capture(nodes) {
    const samples = new Map();
    for (const node of nodes) if (!node.hidden && node.getClientRects().length) samples.set(node, box(node));
    // Read every surface before cancelling any animation (parent/child layout matters).
    for (const node of nodes) {
      const record = active.get(node);
      if (record) {active.delete(node); record.animation.cancel();}
    }
    return samples;
  }
  function settle() {
    for (const [node, record] of active) {
      record.animation.cancel(); node.hidden = record.hidden; node.inert = record.hidden;
      delete node.dataset.moving;
    }
    active.clear();
  }
  function play(nodes, before, endpoint, duration = 340) {
    const pending = [];
    // Measure all final layouts together, before restoring outgoing surfaces.
    for (const node of nodes) {
      const hidden = node.hidden, old = before.get(node);
      if (hidden && !old) {node.inert = true; delete node.dataset.moving; continue;}
      let end;
      if (!hidden) end = box(node);
      else end = endpoint(node, old, false) || {...old, y:old.y+18, opacity:0};
      const start = old || endpoint(node, end, true) || {...end, y:end.y+18, opacity:0};
      pending.push({node,hidden,start,end});
    }
    for (const {node,hidden,start,end} of pending) {
      node.inert = hidden;
      if (reducedMotion.matches || !node.animate ||
          (!hidden && Math.abs(start.x-end.x)+Math.abs(start.y-end.y)+Math.abs(start.width-end.width)+Math.abs(start.height-end.height)<.75 && start.opacity>.99)) {
        node.hidden=hidden; delete node.dataset.moving; continue;
      }
      node.hidden=false;
      const parent=node.offsetParent || canvas, c=canvas.getBoundingClientRect(), p=parent.getBoundingClientRect(), scale=getScale();
      const originX=(p.left-c.left)/scale+parent.clientLeft, originY=(p.top-c.top)/scale+parent.clientTop;
      // Absolute left/top avoids double-applying displacement when a right/bottom
      // anchored surface changes width/height. Content is never scale-transformed.
      const frame = r => ({
        left:`${r.x-originX}px`,top:`${r.y-originY}px`,right:'auto',bottom:'auto',transform:'none',
        width:`${Math.max(1,r.width)}px`, height:`${Math.max(1,r.height)}px`,
        opacity:r.opacity ?? 1, borderRadius:r.radius || '9px'
      });
      node.dataset.moving=hidden?'out':'in';
      const animation=node.animate([frame(start),frame(end)],{
        duration,easing:'cubic-bezier(.22,.78,.22,1)',fill:'both'
      });
      const record={animation,hidden};active.set(node,record);
      animation.finished.then(()=>{
        if(active.get(node)!==record)return;
        node.hidden=hidden;node.inert=hidden;delete node.dataset.moving;
        active.delete(node);animation.cancel();
      }).catch(()=>{});
    }
  }
  return {capture,play,settle,box,isAnimating:()=>active.size>0};
}
