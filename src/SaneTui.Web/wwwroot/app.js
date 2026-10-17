'use strict';

// One page, no framework. The server owns all the state; this draws the latest snapshot it was sent and sends commands.
// What lives only here: which picture is being looked at, the area being selected, and the zoom.

const $ = id => document.getElementById(id);
const ZOOMS = [1, 1.5, 2, 3, 4, 6];
const NUDGE_MM = 1;

let snap = null;          // the latest snapshot from the server
let caps = null;          // what the chosen scanner offers
let devices = null;
let view = 'bed';         // 'bed', or the id of a page of the document
let selection = null;     // the area to scan: { left, top, width, height } in millimeters on the scanner's bed
let zoomIndex = 0;
let known = null;         // page ids already seen, so a new page can be noticed
let previewVersion = 0;
let drawnPicture = '';
let stripSignature = '';
let loading = false;

const busy = () => snap !== null && snap.status !== 'idle';
const rotated = () => $('rotate').checked;
const area = () => snap?.preview?.area ?? null;
const pages = () => snap?.document?.pages ?? [];

function remember(key, value) {
	try { localStorage.setItem(key, value); } catch { /* storage can be blocked; it's only a convenience */ }
}

function recall(key) {
	try { return localStorage.getItem(key); } catch { return null; }
}

async function api(path, options = {}) {
	const response = await fetch(path, { ...options, headers: { 'content-type': 'application/json', ...options.headers } });

	if (!response.ok) {
		let message = `${response.status} ${response.statusText}`;

		try { message = (await response.json()).error ?? message; } catch { /* not JSON */ }

		const error = new Error(message);

		error.status = response.status;

		throw error;
	}

	return response.status === 204 || response.status === 202 ? null : response.json();
}

function say(text, error = false) {
	$('status').textContent = text;
	$('status').classList.toggle('error', error);
}

function fill(select, values, selected, label = v => v) {
	select.replaceChildren(...values.map(v => {
		const option = document.createElement('option');

		option.value = String(v);
		option.textContent = label(v);

		return option;
	}));

	if (selected !== undefined && selected !== null) {
		select.value = String(selected);
	}
}

const capitalized = text => text.charAt(0).toUpperCase() + text.slice(1);

// ---- scanners and what they offer

async function loadDevices(refresh = false) {
	loading = true;

	try {
		devices = await api(`api/devices${refresh ? '?refresh=true' : ''}`);

		if (devices.length === 0) {
			fill($('device'), []);
			say('No scanners found.', true);
		} else {
			fill($('device'), devices.map(d => d.name), recall('device') ?? devices[0].name, name => {
				const d = devices.find(x => x.name === name);

				return `${d.vendor} ${d.model} (${d.name})`;
			});

			if (!devices.some(d => d.name === $('device').value)) {
				$('device').value = devices[0].name;
			}

			await loadCapabilities();
		}
	} catch (error) {
		devices = error.status === 409 ? null : [];  // busy: try again when the scanner is free
		say(error.message, error.status !== 409);
	} finally {
		loading = false;
	}
}

async function loadCapabilities() {
	caps = null;

	const name = $('device').value;

	if (!name) {
		return;
	}

	remember('device', name);

	try {
		caps = await api(`api/capabilities?device=${encodeURIComponent(name)}`);
	} catch (error) {
		say(error.message, error.status !== 409);

		return;
	}

	for (const [id, set] of [['source', caps.sources], ['mode', caps.modes]]) {
		$(`${id}Label`).hidden = set === null;

		if (set) {
			fill($(id), set.choices, set.selected);
		}
	}

	fill($('resolution'), caps.resolutions, caps.defaultResolution, v => `${v} dpi`);
	setPaperChoices('full');
	updateForSource();
	say('Ready.');
}

// a feeder feeds sheets by itself, so scanning everything in it is the sensible default; a preview is of a bed you can lay something on
function updateForSource() {
	const feeder = caps !== null && caps.feeders.includes($('source').value);

	$('allPagesLabel').hidden = !feeder;
	$('allPages').checked = feeder;
	$('preview').hidden = feeder;
}

function setPaperChoices(selected) {
	const values = ['full', ...(selection ? ['selection'] : []), ...(caps?.papers ?? [])];

	fill($('paper'), values, selected, v => v === 'full' ? 'Full area' : v === 'selection' ? 'Selection' : capitalized(v));
}

function setSelection(value) {
	selection = value;
	setPaperChoices(value ? 'selection' : 'full');
	draw();
}

// ---- drawing the latest snapshot

function render() {
	if (!snap) {
		return;
	}

	const wasBusy = busy();
	const preview = snap.preview;

	// a new preview starts with no selection, and is what to look at
	if (preview && preview.version !== previewVersion) {
		previewVersion = preview.version;
		selection = null;
		view = 'bed';
		setPaperChoices('full');
	}

	// a page that just arrived is what to look at; the first snapshot only learns what's there
	const ids = pages().map(p => p.id);

	if (known === null) {
		view = preview ? 'bed' : ids.at(-1) ?? 'bed';
	} else {
		const fresh = ids.filter(id => !known.has(id));

		if (fresh.length > 0) {
			view = fresh.at(-1);
		}
	}

	known = new Set(ids);

	if (view !== 'bed' && !known.has(view)) {
		view = preview ? 'bed' : ids.at(-1) ?? 'bed';
	}

	const last = snap.log.at(-1);

	if (last) {
		say(last.text, last.error);
	}

	$('progress').hidden = snap.progress === null;
	$('progress').value = snap.progress ?? 0;

	for (const id of ['device', 'refresh', 'source', 'mode', 'resolution', 'paper', 'allPages', 'preview', 'scan', 'discard', 'deletePage']) {
		$(id).disabled = wasBusy;
	}

	$('cancel').hidden = !(snap.status === 'previewing' || snap.status === 'scanning');
	$('scan').textContent = snap.status === 'scanning' ? 'Scanning...' : 'Scan';
	$('preview').textContent = snap.status === 'previewing' ? 'Previewing...' : 'Preview';

	renderLog();
	draw();

	// the service was busy when we asked (someone else's scan, or ours a moment ago), so ask again now that it's free
	if (!wasBusy && !loading) {
		if (devices === null) {
			loadDevices();
		} else if (caps === null && devices.length > 0) {
			loadCapabilities();
		}
	}
}

function renderLog() {
	const list = $('log');

	list.replaceChildren(...snap.log.map(line => {
		const item = document.createElement('li');

		item.textContent = `${new Date(line.at).toLocaleTimeString()}  ${line.text}`;
		item.classList.toggle('error', line.error);

		return item;
	}));
}

// the picture, the selection over it, the strip of pages: everything that follows from the snapshot and the local view state
function draw() {
	const doc = snap?.document;
	const onBed = view === 'bed';
	const picture = $('picture');
	let source = '';

	if (onBed && snap?.preview) {
		source = `api/preview.jpg?v=${snap.preview.version}`;
	} else if (!onBed && doc) {
		source = `api/pages/${view}?v=${doc.id}`;
	}

	if (source !== drawnPicture) {
		drawnPicture = source;

		// a different picture is a different size, so a zoom measured on the last one doesn't carry over
		zoomIndex = 0;
		delete $('frame').dataset.fitWidth;

		if (source) {
			picture.src = source;
		} else {
			picture.removeAttribute('src');
		}
	}

	$('frame').classList.toggle('shown', source !== '');
	$('frame').classList.toggle('rotated', onBed && rotated());
	$('placeholder').hidden = source !== '';
	$('hint').hidden = !onBed || !snap?.preview;
	$('zoomControls').hidden = source === '';
	$('deletePage').hidden = onBed || !doc;
	applyZoom();

	const box = $('selection');
	const a = area();

	if (onBed && selection && a) {
		box.hidden = false;
		box.style.left = `${(selection.left - a.x) / a.width * 100}%`;
		box.style.top = `${(selection.top - a.y) / a.height * 100}%`;
		box.style.width = `${selection.width / a.width * 100}%`;
		box.style.height = `${selection.height / a.height * 100}%`;
	} else {
		box.hidden = true;
	}

	if (onBed) {
		$('caption').textContent = !snap?.preview ? '' : selection
			? `Selected ${selection.width.toFixed(0)} x ${selection.height.toFixed(0)} mm, ${selection.left.toFixed(0)} mm across and ${selection.top.toFixed(0)} mm down`
			: 'The whole bed';
	} else {
		const index = pages().findIndex(p => p.id === view);

		$('caption').textContent = index < 0 ? '' : `Page ${index + 1} of ${pages().length}`;
	}

	$('pageCount').textContent = pages().length > 0 ? `(${pages().length} page${pages().length === 1 ? '' : 's'})` : '';
	$('pdf').hidden = pages().length === 0;
	$('discard').hidden = pages().length === 0;

	drawStrip();
}

function drawStrip() {
	const doc = snap?.document;
	const items = [...(snap?.preview ? [{ id: 'bed', label: 'Bed' }] : []), ...pages().map((p, i) => ({ id: p.id, label: `Page ${i + 1}` }))];
	const signature = JSON.stringify([doc?.id, items.map(i => i.id), view]);

	if (signature === stripSignature) {
		return;
	}

	stripSignature = signature;

	$('strip').replaceChildren(...items.map(item => {
		const button = document.createElement('button');
		const image = document.createElement('img');
		const label = document.createElement('span');

		button.type = 'button';
		button.className = 'thumb';
		button.setAttribute('aria-pressed', String(item.id === view));
		image.alt = '';
		image.loading = 'lazy';
		image.src = item.id === 'bed' ? `api/preview.jpg?v=${snap.preview.version}` : `api/pages/${item.id}?w=192&v=${doc.id}`;
		label.textContent = item.label;
		button.append(image, label);
		button.addEventListener('click', () => show(item.id));

		return button;
	}));
}

function show(id) {
	view = id;
	draw();
}

// PageUp and PageDown step through the bed preview and then the pages
function step(direction) {
	const order = [...(snap?.preview ? ['bed'] : []), ...pages().map(p => p.id)];
	const next = order[order.indexOf(view) + direction];

	if (next !== undefined) {
		show(next);
	}
}

// ---- zoom

function applyZoom() {
	const frame = $('frame');
	const picture = $('picture');

	if (zoomIndex === 0) {
		frame.classList.add('fit');
		picture.style.removeProperty('--zoom-width');
		frame.style.removeProperty('--zoom-width');

		return;
	}

	// zoom is a multiple of the size the picture has when it fits the stage
	frame.classList.remove('fit');

	const fit = Number(frame.dataset.fitWidth) || $('stage').clientWidth;

	frame.style.setProperty('--zoom-width', `${fit * ZOOMS[zoomIndex]}px`);
}

function zoom(delta) {
	const frame = $('frame');

	if (zoomIndex === 0) {
		frame.dataset.fitWidth = String($('picture').getBoundingClientRect().width);
	}

	zoomIndex = Math.min(ZOOMS.length - 1, Math.max(0, zoomIndex + delta));

	if (zoomIndex === 0) {
		delete frame.dataset.fitWidth;
	}

	applyZoom();
}

// ---- choosing the area: drag, then fine-tune with the keys

// where a pointer is on the picture as fractions (0 to 1) of its width and height, in the picture's own orientation: a
// rotated preview is shown turned over, so the screen position is turned back
function fractions(event) {
	const r = $('picture').getBoundingClientRect();
	let x = (event.clientX - r.left) / r.width;
	let y = (event.clientY - r.top) / r.height;

	if (rotated()) {
		x = 1 - x;
		y = 1 - y;
	}

	return { x: Math.min(1, Math.max(0, x)), y: Math.min(1, Math.max(0, y)) };
}

function toArea(from, to) {
	const a = area();
	const x1 = Math.min(from.x, to.x), x2 = Math.max(from.x, to.x);
	const y1 = Math.min(from.y, to.y), y2 = Math.max(from.y, to.y);

	return { left: a.x + x1 * a.width, top: a.y + y1 * a.height, width: (x2 - x1) * a.width, height: (y2 - y1) * a.height };
}

function startDrag(event) {
	if (view !== 'bed' || !area() || busy() || event.button !== 0) {
		return;
	}

	const frame = $('frame');
	const origin = fractions(event);
	const startX = event.clientX, startY = event.clientY;
	let moved = false;

	frame.setPointerCapture(event.pointerId);
	$('stage').focus({ preventScroll: true });

	const move = e => {
		moved = moved || Math.hypot(e.clientX - startX, e.clientY - startY) > 3;

		if (moved) {
			selection = toArea(origin, fractions(e));
			draw();
		}
	};

	const finish = e => {
		frame.removeEventListener('pointermove', move);
		frame.removeEventListener('pointerup', finish);
		frame.removeEventListener('pointercancel', finish);

		// a click without a drag clears the area
		setSelection(moved && selection.width > 0 && selection.height > 0 ? selection : null);
	};

	frame.addEventListener('pointermove', move);
	frame.addEventListener('pointerup', finish);
	frame.addEventListener('pointercancel', finish);
	event.preventDefault();
}

// Arrow keys slide the area by a millimeter, Shift+arrow moves its right and bottom edges. Keys mean what's seen on screen, so
// on a rotated preview they're turned around, and the screen's right/bottom edge is the bed's left/top edge.
function nudge(event) {
	const a = area();

	if (!selection || !a || view !== 'bed' || busy()) {
		return;
	}

	const dirs = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
	const dir = dirs[event.key];

	if (!dir) {
		return;
	}

	event.preventDefault();

	const flip = rotated() ? -1 : 1;
	const dx = dir[0] * NUDGE_MM * flip, dy = dir[1] * NUDGE_MM * flip;
	let { left, top, width, height } = selection;

	if (event.shiftKey) {
		width = Math.min(Math.max(width + dx, NUDGE_MM), a.x + a.width - left);
		height = Math.min(Math.max(height + dy, NUDGE_MM), a.y + a.height - top);

		if (rotated()) {
			// the grown edge is the left/top one: keep the right/bottom edge where it was
			const right = selection.left + selection.width, bottom = selection.top + selection.height;

			width = Math.min(Math.max(selection.width - dx, NUDGE_MM), right - a.x);
			height = Math.min(Math.max(selection.height - dy, NUDGE_MM), bottom - a.y);
			left = right - width;
			top = bottom - height;
		}
	} else {
		left = Math.min(Math.max(left + dx, a.x), a.x + a.width - width);
		top = Math.min(Math.max(top + dy, a.y), a.y + a.height - height);
	}

	selection = { left, top, width, height };
	draw();
}

// ---- commands

async function command(path, body) {
	try {
		await api(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) });
	} catch (error) {
		say(error.message, true);
	}
}

function scanRequest() {
	const paper = $('paper').value;
	const request = {
		device: $('device').value,
		source: $('source').hidden ? null : $('source').value,
		mode: $('mode').hidden ? null : $('mode').value,
		resolution: Number($('resolution').value),
		allPages: !$('allPagesLabel').hidden && $('allPages').checked,
		rotate180: rotated(),
	};

	if (paper === 'selection' && selection) {
		Object.assign(request, selection);
	} else if (paper !== 'full') {
		request.paper = paper;
	}

	return request;
}

function wire() {
	$('device').addEventListener('change', () => { loadCapabilities(); });
	$('refresh').addEventListener('click', () => loadDevices(true));
	$('source').addEventListener('change', updateForSource);
	$('rotate').addEventListener('change', draw);
	$('paper').addEventListener('change', () => {
		// choosing anything but the selection gives the selection up, so the box never claims an area the scan won't use
		if (selection && $('paper').value !== 'selection') {
			selection = null;
			setPaperChoices($('paper').value);
			draw();
		}
	});

	$('preview').addEventListener('click', () => command('api/preview', { device: $('device').value, source: $('source').hidden ? null : $('source').value }));
	$('scan').addEventListener('click', () => command('api/scan', scanRequest()));
	$('cancel').addEventListener('click', () => command('api/cancel'));
	$('showLog').addEventListener('click', () => { $('logDialog').showModal(); });

	$('deletePage').addEventListener('click', async () => {
		try { await api(`api/pages/${view}`, { method: 'DELETE' }); } catch (error) { say(error.message, true); }
	});

	$('discard').addEventListener('click', async () => {
		if (!confirm(`Discard all ${pages().length} scanned page(s)? This can't be undone.`)) {
			return;
		}

		try { await api('api/document', { method: 'DELETE' }); } catch (error) { say(error.message, true); }
	});

	$('zoomIn').addEventListener('click', () => zoom(1));
	$('zoomOut').addEventListener('click', () => zoom(-1));
	$('frame').addEventListener('pointerdown', startDrag);
	$('stage').addEventListener('keydown', nudge);

	document.addEventListener('keydown', event => {
		const typing = ['SELECT', 'INPUT', 'TEXTAREA'].includes(event.target.tagName);

		if (!typing && (event.key === 'PageUp' || event.key === 'PageDown')) {
			event.preventDefault();
			step(event.key === 'PageDown' ? 1 : -1);
		}
	});
}

// The browser reconnects an event stream by itself, and the server sends the whole state on every connect.
function connect() {
	const events = new EventSource('api/events');

	events.onopen = () => { $('connection').hidden = true; };
	events.onerror = () => { $('connection').hidden = false; };
	events.onmessage = message => {
		snap = JSON.parse(message.data);
		render();
	};
}

wire();
connect();
loadDevices();
