import * as THREE from 'three';
import { OrbitControls } from '/OrbitControls.js';

const indicators = [
  { pin: 'PG4', ref: 'D20', color: '#ff7068', pixel: [513, 355] },
  { pin: 'PG5', ref: 'D19', color: '#84efad', pixel: [513, 346] },
  { pin: 'PC4', ref: 'D18', color: '#ff7068', pixel: [513, 337] },
  { pin: 'PC5', ref: 'D17', color: '#84efad', pixel: [513, 328] },
];
const canvasHost = document.getElementById('board');
const cardHost = document.getElementById('leds');
const connection = document.getElementById('connection');
const connectionText = document.getElementById('connection-text');
const marker = document.getElementById('marker');
let selected = null;
let view = null;
const cards = indicators.map(({ pin, ref, color }, index) => {
  const card = document.createElement('button');
  card.type = 'button';
  card.className = 'led-card';
  card.setAttribute('aria-pressed', 'false');
  card.style.setProperty('--lamp', color);
  card.innerHTML = `
    <span class="lamp" aria-hidden="true"></span>
    <span><span class="led-name">LED${index}</span><span class="led-pins">${pin} · ${ref}</span></span>
    <span><span class="led-state">—</span><span class="edge-count">—</span></span>`;
  cardHost.append(card);
  card.addEventListener('click', () => {
    selected = selected === index ? null : index;
    cards.forEach(({ card: item }, id) => item.setAttribute('aria-pressed', String(id === selected)));
    view?.draw();
  });
  return { card, state: card.querySelector('.led-state'), changes: card.querySelector('.edge-count') };
});

const frame = { left: 4, top: 28, width: 592, height: 472 };
const xAt = px => ((px - frame.left) / frame.width - 0.5) * 100;
const zAt = px => ((px - frame.top) / frame.height - 0.5) * 80;
const outline = [
  [9, 29], [588, 29], [595, 35], [595, 230], [587, 238], [563, 239], [548, 250],
  [546, 270], [557, 290], [579, 294], [587, 301], [587, 394], [595, 406], [595, 490],
  [588, 500], [9, 500], [4, 493], [4, 303], [10, 292], [25, 292], [43, 282],
  [48, 264], [43, 245], [29, 238], [10, 238], [5, 229], [5, 39],
];

function boardShape() {
  const result = new THREE.Shape();
  outline.forEach(([x, y], index) => {
    if (index === 0) result.moveTo(xAt(x), -zAt(y));
    else result.lineTo(xAt(x), -zAt(y));
  });
  result.closePath();
  return result;
}

function imageUv(geometry, xOffset = 0, zOffset = 0, rotated = false) {
  const vertices = geometry.attributes.position;
  const uv = geometry.attributes.uv;
  for (let index = 0; index < vertices.count; index++) {
    const x = vertices.getX(index) + xOffset;
    const z = rotated ? -vertices.getY(index) : vertices.getZ(index) + zOffset;
    const horizontal = (frame.left + (x / 100 + 0.5) * frame.width) / 600;
    const vertical = 1 - (frame.top + (z / 80 + 0.5) * frame.height) / 502;
    uv.setXY(index, horizontal, vertical);
  }
}

function glowTexture() {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 64;
  const context = canvas.getContext('2d');
  const radial = context.createRadialGradient(32, 32, 1, 32, 32, 31);
  radial.addColorStop(0, 'rgba(255,255,255,0.92)');
  radial.addColorStop(0.25, 'rgba(255,255,255,0.52)');
  radial.addColorStop(1, 'rgba(255,255,255,0)');
  context.fillStyle = radial;
  context.fillRect(0, 0, 64, 64);
  return new THREE.CanvasTexture(canvas);
}

function createBoard() {
  const renderer = new THREE.WebGLRenderer({ alpha: true, antialias: true });
  renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
  renderer.setClearColor(0x000000, 0);
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.25;
  renderer.domElement.setAttribute('aria-label', 'ISBC 상면 사진 기반 3D 보드');
  canvasHost.append(renderer.domElement);
  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(34, 1, 0.5, 900);
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enablePan = false;
  controls.minDistance = 55;
  controls.maxDistance = 330;
  controls.maxPolarAngle = Math.PI * 0.53;
  controls.target.set(0, -5, 0);

  scene.add(new THREE.HemisphereLight(0xf6fffb, 0xa19c98, 2.25));
  const key = new THREE.DirectionalLight(0xfff5e7, 2.2);
  key.position.set(-55, 130, 80);
  scene.add(key);

  const grid = new THREE.GridHelper(300, 30, 0x3c4646, 0x343c3c);
  grid.position.y = -20;
  grid.material.transparent = true;
  grid.material.opacity = 0.52;
  scene.add(grid);
  const shadow = new THREE.Mesh(
    new THREE.PlaneGeometry(164, 129),
    new THREE.MeshBasicMaterial({ map: glowTexture(), color: 0x000000, transparent: true, opacity: 0.72, depthWrite: false }),
  );
  shadow.rotation.x = -Math.PI / 2;
  shadow.position.y = -19.8;
  scene.add(shadow);

  const shape = boardShape();
  const bodyMaterial = new THREE.MeshStandardMaterial({ color: 0x823837, roughness: 0.78 });
  const edgeMaterial = new THREE.MeshStandardMaterial({ color: 0x967267, roughness: 0.87 });
  const black = new THREE.MeshStandardMaterial({ color: 0x1f2427, roughness: 0.57 });
  const silver = new THREE.MeshStandardMaterial({ color: 0xa2a9aa, roughness: 0.43, metalness: 0.3 });
  const white = new THREE.MeshStandardMaterial({ color: 0xbfbfa6, roughness: 0.66 });
  const brass = new THREE.MeshStandardMaterial({ color: 0xb59a57, roughness: 0.4, metalness: 0.4 });

  for (const y of [-12.6, -1.6]) {
    const geometry = new THREE.ExtrudeGeometry(shape, { depth: 1.6, bevelEnabled: false });
    const pcb = new THREE.Mesh(geometry, [bodyMaterial, edgeMaterial]);
    pcb.rotation.x = -Math.PI / 2;
    pcb.position.y = y;
    scene.add(pcb);
  }
  const photoMap = new THREE.TextureLoader().load('/board-top.png', draw);
  photoMap.colorSpace = THREE.SRGBColorSpace;
  photoMap.anisotropy = Math.min(renderer.capabilities.getMaxAnisotropy(), 8);
  const topMaterial = new THREE.MeshBasicMaterial({ map: photoMap, toneMapped: false });
  const surface = new THREE.ShapeGeometry(shape);
  imageUv(surface, 0, 0, true);
  const face = new THREE.Mesh(surface, topMaterial);
  face.rotation.x = -Math.PI / 2;
  face.position.y = 0.025;
  scene.add(face);

  function component(left, top, right, bottom, height, sides, base = 0, textured = true) {
    const x = (xAt(left) + xAt(right)) / 2;
    const z = (zAt(top) + zAt(bottom)) / 2;
    const geometry = new THREE.BoxGeometry(xAt(right) - xAt(left), height, zAt(bottom) - zAt(top));
    imageUv(geometry, x, z);
    const materials = textured ? [sides, sides, topMaterial, sides, sides, sides] : sides;
    const piece = new THREE.Mesh(geometry, materials);
    piece.position.set(x, base + height / 2, z);
    scene.add(piece);
  }

  component(503, 81, 595, 181, 10.5, silver);
  component(374, 124, 455, 207, 1.6, black);
  component(264, 253, 326, 316, 1.4, black);
  component(547, 300, 584, 387, 5.2, white);
  component(543, 410, 578, 452, 3.7, silver);
  component(216, 29, 310, 57, 5.8, black);
  for (const [top, bottom] of [[74, 123], [132, 186], [197, 250], [258, 312]]) {
    component(94, top, 135, bottom, 7, black);
  }
  for (const [left, right] of [[4, 36], [151, 208], [224, 291], [309, 343], [425, 455]]) {
    component(left, 471, right, 497, 4.7, black);
  }
  component(224, 397, 303, 427, 3.8, black);
  component(8, 34, 35, 180, 7.8, black, -11, false);
  component(8, 316, 35, 489, 7.8, black, -11, false);
  component(500, 320, 588, 368, 8.8, new THREE.MeshStandardMaterial({ color: 0x337867 }), -11, false);
  component(7, 29, 592, 499, 1.2, silver, -16.8, false);
  for (const [x, y] of [[117, 50], [570, 51], [117, 475], [568, 477]]) {
    const stand = new THREE.Mesh(new THREE.CylinderGeometry(2.65, 2.65, 17, 12), brass);
    stand.position.set(xAt(x), -7.6, zAt(y));
    scene.add(stand);
    const slot = new THREE.Mesh(new THREE.CylinderGeometry(1.35, 1.35, 0.16, 16), black);
    slot.position.set(xAt(x), 1, zAt(y));
    scene.add(slot);
  }

  const haloMap = glowTexture();
  const lights = indicators.map(({ pixel, color }) => {
    const body = new THREE.MeshStandardMaterial({
      color: 0x9a997f, emissive: color, emissiveIntensity: 0, roughness: 0.32,
    });
    const lens = new THREE.Mesh(new THREE.BoxGeometry(2.12, 0.5, 0.96), body);
    lens.position.set(xAt(pixel[0]), 0.33, zAt(pixel[1]));
    scene.add(lens);
    const halo = new THREE.Sprite(new THREE.SpriteMaterial({
      map: haloMap, color, blending: THREE.AdditiveBlending, transparent: true, depthWrite: false, opacity: 0.28,
    }));
    halo.position.copy(lens.position);
    halo.position.y += 0.32;
    halo.scale.set(4, 4, 1);
    halo.visible = false;
    scene.add(halo);
    return { lens, halo, color };
  });

  const modeButtons = [document.getElementById('top-view'), document.getElementById('iso-view')];
  modeButtons[0].onclick = () => setView(true);
  modeButtons[1].onclick = () => setView(false);
  function setView(top) {
    const distance = Math.max(180, 176 / Math.min(1.15, camera.aspect));
    if (top) camera.position.set(0, distance, 0.001);
    else camera.position.copy(new THREE.Vector3(0.46, 0.65, 0.74).normalize().multiplyScalar(distance));
    modeButtons.forEach((button, index) => button.setAttribute('aria-pressed', String(index === (top ? 0 : 1))));
    controls.update();
    draw();
  }
  function draw() {
    renderer.render(scene, camera);
    marker.hidden = selected === null;
    if (selected !== null) {
      const point = lights[selected].lens.position.clone().project(camera);
      marker.textContent = `LED${selected}`;
      marker.style.left = `${(point.x + 1) * 0.5 * canvasHost.clientWidth}px`;
      marker.style.top = `${(1 - point.y) * 0.5 * canvasHost.clientHeight}px`;
      marker.hidden = Math.abs(point.x) > 0.92 || Math.abs(point.y) > 0.95;
    }
  }
  function resize() {
    const width = canvasHost.clientWidth;
    const height = canvasHost.clientHeight;
    if (!width || !height) return;
    camera.aspect = width / height;
    camera.updateProjectionMatrix();
    renderer.setSize(width, height, false);
  }
  resize();
  setView(false);
  controls.addEventListener('change', draw);
  new ResizeObserver(() => { resize(); draw(); }).observe(canvasHost);
  return {
    draw,
    display(values) {
      lights.forEach(({ lens, halo, color }, index) => {
        const on = values[index]?.on === true;
        lens.material.color.set(on ? color : 0x99967e);
        lens.material.emissiveIntensity = on ? 1.15 : 0;
        halo.visible = on;
      });
      draw();
    },
  };
}

try {
  view = createBoard();
} catch (error) {
  document.getElementById('render-error').hidden = false;
  console.error(error);
}

let displayed = '';
async function refresh() {
  let state = 'offline';
  let leds = [];
  try {
    const response = await fetch('/state', { cache: 'no-store', signal: AbortSignal.timeout(1400) });
    if (!response.ok) throw new Error('Renode state not available');
    const data = await response.json();
    state = data.running ? 'running' : 'paused';
    leds = data.leds;
  } catch {
    // Clear stale light readings until the service reconnects.
  }
  const signature = JSON.stringify([state, leds]);
  if (displayed !== signature) {
    displayed = signature;
    connection.dataset.state = state;
    connectionText.textContent = { running: 'Renode 실행 중', paused: 'Renode 일시 정지', offline: 'Renode 연결 끊김' }[state];
    cards.forEach(({ card, state: text, changes }, index) => {
      const led = leds[index];
      card.dataset.on = String(led?.on === true);
      text.textContent = led?.on == null ? '—' : led.on ? '켜짐' : '꺼짐';
      changes.textContent = led?.high == null ? '—' : `${led.high ? 'HIGH' : 'LOW'} · ${led.changes}회`;
    });
    view?.display(leds);
  }
  setTimeout(refresh, state === 'offline' ? 900 : 100);
}
refresh();
