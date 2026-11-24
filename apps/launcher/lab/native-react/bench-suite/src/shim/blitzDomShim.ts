declare const __blitz_batch: (json: string) => number;
declare const __blitz_query: (json: string) => string;
declare const __blitz_log: (level: string, msg: string) => void;

type ListenerEntry = { fn: Function; capture: boolean; once: boolean };

let nextNodeId = 10;
let nextAnimId = 1;
let nextTimerId = 1;
let simNowMs = 0;

const nodeById = new Map<number, BlitzNode>();
const pendingOps: any[] = [];
const rafQueue = new Map<number, (t: number) => void>();
const timers = new Map<number, { fn: Function; at: number; interval: number | null; args: any[] }>();
const resizeObservers = new Set<BlitzResizeObserver>();
const intersectionObservers = new Set<BlitzIntersectionObserver>();
const mutationObservers = new Set<BlitzMutationObserver>();
const activeCanvas2ds = new Set<BlitzCanvas2dContext>();

function flushOps(): void {
  for (const ctx of activeCanvas2ds) {
    ctx._flushIfDirty();
  }
  if (pendingOps.length === 0) return;
  const payload = JSON.stringify(pendingOps);
  pendingOps.length = 0;
  __blitz_batch(payload);
}

function querySync(obj: any): any {
  flushOps();
  const raw = __blitz_query(JSON.stringify(obj));
  try {
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

function camelToKebab(k: string): string {
  if (k.startsWith('--') || k.includes('-')) return k;
  return k.replace(/[A-Z]/g, (m) => '-' + m.toLowerCase());
}

function kebabToCamel(k: string): string {
  return k.replace(/-([a-z])/g, (_, c) => c.toUpperCase());
}

class BlitzEvent {
  type: string;
  bubbles: boolean;
  cancelable: boolean;
  composed: boolean;
  defaultPrevented = false;
  cancelBubble = false;
  immediatePropagationStopped = false;
  target: any = null;
  currentTarget: any = null;
  eventPhase = 0;
  timeStamp: number;
  isTrusted = true;
  detail: any;

  constructor(type: string, init: any = {}) {
    this.type = String(type);
    this.bubbles = Boolean(init.bubbles);
    this.cancelable = Boolean(init.cancelable);
    this.composed = Boolean(init.composed);
    this.detail = init.detail ?? null;
    this.timeStamp = simNowMs;
  }
  preventDefault() {
    if (this.cancelable) this.defaultPrevented = true;
  }
  stopPropagation() {
    this.cancelBubble = true;
  }
  stopImmediatePropagation() {
    this.cancelBubble = true;
    this.immediatePropagationStopped = true;
  }
  composedPath() {
    const path: any[] = [];
    let cur = this.target;
    while (cur) {
      path.push(cur);
      cur = cur.parentNode;
    }
    path.push(globalThis);
    return path;
  }
}

class BlitzUIEvent extends BlitzEvent {
  view: any;
  constructor(type: string, init: any = {}) {
    super(type, init);
    this.view = init.view ?? globalThis;
  }
}

class BlitzMouseEvent extends BlitzUIEvent {
  clientX: number;
  clientY: number;
  pageX: number;
  pageY: number;
  screenX: number;
  screenY: number;
  button: number;
  buttons: number;
  ctrlKey: boolean;
  shiftKey: boolean;
  altKey: boolean;
  metaKey: boolean;
  relatedTarget: any;
  constructor(type: string, init: any = {}) {
    super(type, init);
    this.clientX = init.clientX ?? 0;
    this.clientY = init.clientY ?? 0;
    this.pageX = init.pageX ?? this.clientX;
    this.pageY = init.pageY ?? this.clientY;
    this.screenX = init.screenX ?? this.clientX;
    this.screenY = init.screenY ?? this.clientY;
    this.button = init.button ?? 0;
    this.buttons = init.buttons ?? 0;
    this.ctrlKey = Boolean(init.ctrlKey);
    this.shiftKey = Boolean(init.shiftKey);
    this.altKey = Boolean(init.altKey);
    this.metaKey = Boolean(init.metaKey);
    this.relatedTarget = init.relatedTarget ?? null;
  }
  getModifierState(k: string) {
    if (k === 'Control') return this.ctrlKey;
    if (k === 'Shift') return this.shiftKey;
    if (k === 'Alt') return this.altKey;
    if (k === 'Meta') return this.metaKey;
    return false;
  }
}

class BlitzPointerEvent extends BlitzMouseEvent {
  pointerId: number;
  pointerType: string;
  isPrimary: boolean;
  pressure: number;
  width: number;
  height: number;
  constructor(type: string, init: any = {}) {
    super(type, init);
    this.pointerId = init.pointerId ?? 1;
    this.pointerType = init.pointerType ?? 'mouse';
    this.isPrimary = init.isPrimary ?? true;
    this.pressure = init.pressure ?? 0.5;
    this.width = init.width ?? 1;
    this.height = init.height ?? 1;
  }
}

class BlitzKeyboardEvent extends BlitzUIEvent {
  key: string;
  code: string;
  keyCode: number;
  which: number;
  ctrlKey: boolean;
  shiftKey: boolean;
  altKey: boolean;
  metaKey: boolean;
  repeat: boolean;
  constructor(type: string, init: any = {}) {
    super(type, init);
    this.key = init.key ?? '';
    this.code = init.code ?? '';
    this.keyCode = init.keyCode ?? 0;
    this.which = init.which ?? this.keyCode;
    this.ctrlKey = Boolean(init.ctrlKey);
    this.shiftKey = Boolean(init.shiftKey);
    this.altKey = Boolean(init.altKey);
    this.metaKey = Boolean(init.metaKey);
    this.repeat = Boolean(init.repeat);
  }
  getModifierState(k: string) {
    if (k === 'Control') return this.ctrlKey;
    if (k === 'Shift') return this.shiftKey;
    if (k === 'Alt') return this.altKey;
    if (k === 'Meta') return this.metaKey;
    return false;
  }
}

class BlitzFocusEvent extends BlitzUIEvent {
  relatedTarget: any;
  constructor(type: string, init: any = {}) {
    super(type, init);
    this.relatedTarget = init.relatedTarget ?? null;
  }
}

class BlitzEventTarget {
  _listeners = new Map<string, ListenerEntry[]>();

  addEventListener(type: string, fn: any, opts?: any) {
    if (!fn) return;
    const capture = typeof opts === 'boolean' ? opts : Boolean(opts?.capture);
    const once = typeof opts === 'object' && Boolean(opts?.once);
    const list = this._listeners.get(type) ?? [];
    if (!list.some((l) => l.fn === fn && l.capture === capture)) {
      list.push({ fn, capture, once });
      this._listeners.set(type, list);
    }
  }

  removeEventListener(type: string, fn: any, opts?: any) {
    const capture = typeof opts === 'boolean' ? opts : Boolean(opts?.capture);
    const list = this._listeners.get(type);
    if (!list) return;
    this._listeners.set(
      type,
      list.filter((l) => !(l.fn === fn && l.capture === capture)),
    );
  }

  dispatchEvent(ev: BlitzEvent): boolean {
    if (!ev.target) ev.target = this;
    const path: any[] = [];
    let cur: any = this;
    while (cur) {
      path.push(cur);
      cur = cur.parentNode ?? (cur === documentObj ? windowObj : null);
    }
    for (let i = path.length - 1; i >= 1; i--) {
      if (ev.cancelBubble) break;
      ev.eventPhase = 1;
      path[i]._invokeListeners(ev, true);
    }
    if (!ev.cancelBubble) {
      ev.eventPhase = 2;
      this._invokeListeners(ev, true);
      if (!ev.immediatePropagationStopped) {
        this._invokeListeners(ev, false);
      }
    }
    if (ev.bubbles) {
      for (let i = 1; i < path.length; i++) {
        if (ev.cancelBubble) break;
        ev.eventPhase = 3;
        path[i]._invokeListeners(ev, false);
      }
    }
    return !ev.defaultPrevented;
  }

  _invokeListeners(ev: BlitzEvent, capture: boolean) {
    const list = this._listeners.get(ev.type);
    if (list && list.length > 0) {
      ev.currentTarget = this;
      for (const entry of [...list]) {
        if (entry.capture !== capture) continue;
        if (ev.immediatePropagationStopped) break;
        if (entry.once) {
          this.removeEventListener(ev.type, entry.fn, capture);
        }
        try {
          if (typeof entry.fn === 'function') {
            entry.fn.call(this, ev);
          } else if (typeof entry.fn?.handleEvent === 'function') {
            entry.fn.handleEvent(ev);
          }
        } catch (err: any) {
          __blitz_log('error', String(err?.stack || err));
        }
      }
    }
    if (!capture) {
      const propHandler = (this as any)['on' + ev.type];
      if (typeof propHandler === 'function') {
        try {
          propHandler.call(this, ev);
        } catch (err: any) {
          __blitz_log('error', String(err?.stack || err));
        }
      }
    }
  }
}

class BlitzNode extends BlitzEventTarget {
  static ELEMENT_NODE = 1;
  static TEXT_NODE = 3;
  static COMMENT_NODE = 8;
  static DOCUMENT_NODE = 9;
  static DOCUMENT_FRAGMENT_NODE = 11;

  _id: number;
  nodeType: number;
  nodeName: string;
  parentNode: BlitzNode | null = null;
  childNodes: BlitzNode[] = [];
  ownerDocument: any;

  constructor(id: number, nodeType: number, nodeName: string) {
    super();
    this._id = id;
    this.nodeType = nodeType;
    this.nodeName = nodeName;
    this.ownerDocument = documentObj;
    if (id > 0) nodeById.set(id, this);
  }

  get parentElement(): BlitzElement | null {
    return this.parentNode instanceof BlitzElement ? this.parentNode : null;
  }
  get firstChild(): BlitzNode | null {
    return this.childNodes[0] ?? null;
  }
  get lastChild(): BlitzNode | null {
    return this.childNodes[this.childNodes.length - 1] ?? null;
  }
  get nextSibling(): BlitzNode | null {
    if (!this.parentNode) return null;
    const idx = this.parentNode.childNodes.indexOf(this);
    return idx >= 0 ? this.parentNode.childNodes[idx + 1] ?? null : null;
  }
  get previousSibling(): BlitzNode | null {
    if (!this.parentNode) return null;
    const idx = this.parentNode.childNodes.indexOf(this);
    return idx > 0 ? this.parentNode.childNodes[idx - 1] ?? null : null;
  }
  get isConnected(): boolean {
    let cur: BlitzNode | null = this;
    while (cur) {
      if (cur._id === 1 || cur === documentObj) return true;
      cur = cur.parentNode;
    }
    return false;
  }
  getRootNode(): BlitzNode {
    let cur: BlitzNode = this;
    while (cur.parentNode) cur = cur.parentNode;
    return cur;
  }
  contains(other: BlitzNode | null): boolean {
    let cur = other;
    while (cur) {
      if (cur === this) return true;
      cur = cur.parentNode;
    }
    return false;
  }
  compareDocumentPosition(other: BlitzNode): number {
    if (this === other) return 0;
    if (this.contains(other)) return 20;
    if (other.contains(this)) return 10;
    return 4;
  }
  hasChildNodes(): boolean {
    return this.childNodes.length > 0;
  }
  matches(_selector: string): boolean {
    return false;
  }
  closest(_selector: string): BlitzElement | null {
    return null;
  }
  appendChild<T extends BlitzNode>(child: T): T {
    if (child.nodeType === 11) {
      for (const c of [...child.childNodes]) {
        this.appendChild(c);
      }
      return child;
    }
    if (child.parentNode) {
      child.parentNode.removeChild(child);
    }
    child.parentNode = this;
    this.childNodes.push(child);
    if (this._id > 0 && child._id > 0) {
      pendingOps.push({ op: 'appendChild', parent: this._id, child: child._id });
    }
    notifyMutation(this, 'childList', [child], []);
    return child;
  }
  insertBefore<T extends BlitzNode>(child: T, anchor: BlitzNode | null): T {
    if (!anchor) return this.appendChild(child);
    if (child.nodeType === 11) {
      for (const c of [...child.childNodes]) {
        this.insertBefore(c, anchor);
      }
      return child;
    }
    if (child.parentNode) {
      child.parentNode.removeChild(child);
    }
    const idx = this.childNodes.indexOf(anchor);
    if (idx < 0) return this.appendChild(child);
    child.parentNode = this;
    this.childNodes.splice(idx, 0, child);
    if (this._id > 0 && child._id > 0 && anchor._id > 0) {
      pendingOps.push({
        op: 'insertBefore',
        parent: this._id,
        child: child._id,
        anchor: anchor._id,
      });
    }
    notifyMutation(this, 'childList', [child], []);
    return child;
  }
  removeChild<T extends BlitzNode>(child: T): T {
    const idx = this.childNodes.indexOf(child);
    if (idx >= 0) {
      this.childNodes.splice(idx, 1);
      child.parentNode = null;
      if (this._id > 0 && child._id > 0) {
        pendingOps.push({ op: 'removeChild', parent: this._id, child: child._id });
      }
      notifyMutation(this, 'childList', [], [child]);
    }
    return child;
  }
  replaceChild<T extends BlitzNode>(next: BlitzNode, prev: T): T {
    this.insertBefore(next, prev);
    return this.removeChild(prev);
  }
  remove() {
    if (this.parentNode) this.parentNode.removeChild(this);
  }
  get textContent(): string {
    return this.childNodes.map((c) => c.textContent).join('');
  }
  set textContent(v: string) {
    const s = v == null ? '' : String(v);
    for (const c of [...this.childNodes]) {
      this.removeChild(c);
    }
    if (s.length > 0) {
      const tn = documentObj.createTextNode(s);
      this.appendChild(tn);
    }
  }
  cloneNode(deep = false): BlitzNode {
    const copy =
      this.nodeType === 3
        ? documentObj.createTextNode((this as any)._data)
        : this.nodeType === 8
          ? documentObj.createComment((this as any)._data)
          : documentObj.createElement((this as any).tagName.toLowerCase());
    if (this instanceof BlitzElement && copy instanceof BlitzElement) {
      for (const [k, v] of this._attrs) copy.setAttribute(k, v);
    }
    if (deep) {
      for (const c of this.childNodes) copy.appendChild(c.cloneNode(true));
    }
    return copy;
  }
}

class BlitzText extends BlitzNode {
  _data: string;
  constructor(id: number, text: string) {
    super(id, 3, '#text');
    this._data = String(text ?? '');
  }
  get data() {
    return this._data;
  }
  set data(v: string) {
    this._data = String(v ?? '');
    pendingOps.push({ op: 'setText', id: this._id, text: this._data });
  }
  get nodeValue() {
    return this._data;
  }
  set nodeValue(v: string) {
    this.data = v;
  }
  get textContent() {
    return this._data;
  }
  set textContent(v: string) {
    this.data = v;
  }
  get length() {
    return this._data.length;
  }
}

class BlitzComment extends BlitzNode {
  _data: string;
  constructor(id: number, text: string) {
    super(id, 8, '#comment');
    this._data = String(text ?? '');
  }
  get data() {
    return this._data;
  }
  set data(v: string) {
    this._data = String(v ?? '');
  }
  get nodeValue() {
    return this._data;
  }
  set nodeValue(v: string) {
    this._data = String(v ?? '');
  }
  get textContent() {
    return this._data;
  }
}

class BlitzDocumentFragment extends BlitzNode {
  constructor() {
    super(0, 11, '#document-fragment');
  }
  get children() {
    return this.childNodes.filter((n) => n.nodeType === 1);
  }
}

class BlitzClassList {
  el: BlitzElement;
  constructor(el: BlitzElement) {
    this.el = el;
  }
  _tokens(): string[] {
    const raw = this.el.getAttribute('class') || '';
    return raw.trim() ? raw.trim().split(/\s+/) : [];
  }
  _save(tokens: string[]) {
    this.el.setAttribute('class', tokens.join(' '));
  }
  add(...names: string[]) {
    const set = new Set(this._tokens());
    for (const n of names) if (n) set.add(n);
    this._save([...set]);
  }
  remove(...names: string[]) {
    const drop = new Set(names);
    this._save(this._tokens().filter((t) => !drop.has(t)));
  }
  toggle(name: string, force?: boolean): boolean {
    const has = this.contains(name);
    const want = force === undefined ? !has : Boolean(force);
    if (want) this.add(name);
    else this.remove(name);
    return want;
  }
  contains(name: string): boolean {
    return this._tokens().includes(name);
  }
  replace(a: string, b: string) {
    if (!this.contains(a)) return false;
    this.remove(a);
    this.add(b);
    return true;
  }
  get value() {
    return this.el.getAttribute('class') || '';
  }
  set value(v: string) {
    this.el.setAttribute('class', v);
  }
  get length() {
    return this._tokens().length;
  }
  [Symbol.iterator]() {
    return this._tokens()[Symbol.iterator]();
  }
}

function makeStyleProxy(el: BlitzElement) {
  const map = new Map<string, string>();
  const api: any = {
    setProperty(prop: string, val: string) {
      const k = camelToKebab(String(prop));
      if (val === '' || val == null) {
        map.delete(k);
        pendingOps.push({ op: 'removeStyleProp', id: el._id, name: k });
      } else {
        const s = String(val);
        map.set(k, s);
        pendingOps.push({ op: 'setStyleProp', id: el._id, name: k, value: s });
      }
    },
    removeProperty(prop: string) {
      const k = camelToKebab(String(prop));
      const old = map.get(k) ?? '';
      map.delete(k);
      pendingOps.push({ op: 'removeStyleProp', id: el._id, name: k });
      return old;
    },
    getPropertyValue(prop: string) {
      return map.get(camelToKebab(String(prop))) ?? '';
    },
    get cssText() {
      let out = '';
      for (const [k, v] of map) out += `${k}:${v};`;
      return out;
    },
    set cssText(text: string) {
      map.clear();
      for (const decl of String(text || '').split(';')) {
        const idx = decl.indexOf(':');
        if (idx > 0) {
          const k = decl.slice(0, idx).trim();
          const v = decl.slice(idx + 1).trim();
          api.setProperty(k, v);
        }
      }
    },
  };
  return new Proxy(api, {
    get(target, prop) {
      if (typeof prop === 'symbol') return (target as any)[prop];
      if (prop in target) return (target as any)[prop];
      return map.get(camelToKebab(String(prop))) ?? '';
    },
    set(target, prop, val) {
      if (prop === 'cssText') {
        target.cssText = val;
        return true;
      }
      target.setProperty(String(prop), val == null ? '' : String(val));
      return true;
    },
  });
}

class BlitzElement extends BlitzNode {
  tagName: string;
  localName: string;
  namespaceURI: string;
  _attrs = new Map<string, string>();
  classList: BlitzClassList;
  style: any;
  dataset: any;
  scrollTop = 0;
  scrollLeft = 0;
  _value = '';
  _checked = false;
  _disabled = false;
  _tabIndex = -1;
  _sheet: any = null;
  _ctx2d: BlitzCanvas2dContext | null = null;
  _ctxGl: any = null;
  _canvasW = 300;
  _canvasH = 150;

  constructor(id: number, tag: string, nsUri = 'http://www.w3.org/1999/xhtml') {
    super(id, 1, nsUri.includes('svg') ? tag : tag.toUpperCase());
    this.tagName = this.nodeName;
    this.localName = tag.toLowerCase();
    this.namespaceURI = nsUri;
    this.classList = new BlitzClassList(this);
    this.style = makeStyleProxy(this);
    const self = this;
    this.dataset = new Proxy(
      {},
      {
        get(_t, p) {
          if (typeof p !== 'string') return undefined;
          return self.getAttribute('data-' + camelToKebab(p)) ?? undefined;
        },
        set(_t, p, v) {
          if (typeof p === 'string') self.setAttribute('data-' + camelToKebab(p), String(v));
          return true;
        },
        deleteProperty(_t, p) {
          if (typeof p === 'string') self.removeAttribute('data-' + camelToKebab(p));
          return true;
        },
      },
    );
    if (this.localName === 'style') {
      const rules: string[] = [];
      this._sheet = {
        cssRules: rules,
        rules,
        insertRule(rule: string, idx = rules.length) {
          rules.splice(idx, 0, rule);
          pendingOps.push({ op: 'addStyleText', id: self._id, css: rules.join('\n') });
          return idx;
        },
        deleteRule(idx: number) {
          rules.splice(idx, 1);
          pendingOps.push({ op: 'addStyleText', id: self._id, css: rules.join('\n') });
        },
      };
    }
  }

  get sheet() {
    return this._sheet;
  }
  get id() {
    return this.getAttribute('id') ?? '';
  }
  set id(v: string) {
    this.setAttribute('id', v);
  }
  get className(): any {
    if (this.namespaceURI.includes('svg')) {
      return { baseVal: this.getAttribute('class') ?? '', animVal: this.getAttribute('class') ?? '' };
    }
    return this.getAttribute('class') ?? '';
  }
  set className(v: any) {
    this.setAttribute('class', typeof v === 'string' ? v : String(v?.baseVal ?? ''));
  }
  get value() {
    return this._value;
  }
  set value(v: string) {
    this._value = String(v ?? '');
    this.setAttribute('value', this._value);
  }
  get checked() {
    return this._checked;
  }
  set checked(v: boolean) {
    this._checked = Boolean(v);
    if (this._checked) this.setAttribute('checked', '');
    else this.removeAttribute('checked');
  }
  get disabled() {
    return this._disabled;
  }
  set disabled(v: boolean) {
    this._disabled = Boolean(v);
    if (this._disabled) this.setAttribute('disabled', '');
    else this.removeAttribute('disabled');
  }
  get tabIndex() {
    const a = this.getAttribute('tabindex');
    if (a != null) return parseInt(a, 10) || 0;
    return this._tabIndex;
  }
  set tabIndex(v: number) {
    this._tabIndex = Number(v);
    this.setAttribute('tabindex', String(this._tabIndex));
  }
  get width() {
    return this._canvasW;
  }
  set width(v: number) {
    this._canvasW = Math.max(1, Number(v) || 300);
    this.setAttribute('width', String(this._canvasW));
    if (this.localName === 'canvas') {
      pendingOps.push({ op: 'canvasSize', id: this._id, width: this._canvasW, height: this._canvasH });
    }
  }
  get height() {
    return this._canvasH;
  }
  set height(v: number) {
    this._canvasH = Math.max(1, Number(v) || 150);
    this.setAttribute('height', String(this._canvasH));
    if (this.localName === 'canvas') {
      pendingOps.push({ op: 'canvasSize', id: this._id, width: this._canvasW, height: this._canvasH });
    }
  }
  get children(): BlitzElement[] {
    return this.childNodes.filter((n): n is BlitzElement => n.nodeType === 1);
  }
  get childElementCount() {
    return this.children.length;
  }
  get firstElementChild() {
    return this.children[0] ?? null;
  }
  get lastElementChild() {
    const c = this.children;
    return c[c.length - 1] ?? null;
  }
  get nextElementSibling(): BlitzElement | null {
    let n = this.nextSibling;
    while (n) {
      if (n instanceof BlitzElement) return n;
      n = n.nextSibling;
    }
    return null;
  }
  get previousElementSibling(): BlitzElement | null {
    let n = this.previousSibling;
    while (n) {
      if (n instanceof BlitzElement) return n;
      n = n.previousSibling;
    }
    return null;
  }
  get attributes() {
    const arr: any[] = [];
    for (const [name, value] of this._attrs) arr.push({ name, value });
    return arr;
  }

  getAttribute(name: string): string | null {
    return this._attrs.get(name.toLowerCase()) ?? null;
  }
  hasAttribute(name: string): boolean {
    return this._attrs.has(name.toLowerCase());
  }
  setAttribute(name: string, value: string) {
    const k = name.toLowerCase();
    const s = String(value ?? '');
    this._attrs.set(k, s);
    if (k === 'style') {
      this.style.cssText = s;
    }
    if (k === 'width' && this.localName === 'canvas') {
      this._canvasW = Math.max(1, parseInt(s, 10) || 300);
      pendingOps.push({ op: 'canvasSize', id: this._id, width: this._canvasW, height: this._canvasH });
    }
    if (k === 'height' && this.localName === 'canvas') {
      this._canvasH = Math.max(1, parseInt(s, 10) || 150);
      pendingOps.push({ op: 'canvasSize', id: this._id, width: this._canvasW, height: this._canvasH });
    }
    pendingOps.push({ op: 'setAttr', id: this._id, name: k, value: s });
    notifyMutation(this, 'attributes', [], [], k);
  }
  setAttributeNS(_ns: string | null, name: string, value: string) {
    this.setAttribute(name, value);
  }
  removeAttribute(name: string) {
    const k = name.toLowerCase();
    this._attrs.delete(k);
    pendingOps.push({ op: 'removeAttr', id: this._id, name: k });
    notifyMutation(this, 'attributes', [], [], k);
  }
  removeAttributeNS(_ns: string | null, name: string) {
    this.removeAttribute(name);
  }
  toggleAttribute(name: string, force?: boolean): boolean {
    const has = this.hasAttribute(name);
    const want = force === undefined ? !has : Boolean(force);
    if (want) this.setAttribute(name, '');
    else this.removeAttribute(name);
    return want;
  }
  getAttributeNames() {
    return [...this._attrs.keys()];
  }

  get innerHTML(): string {
    return this.textContent;
  }
  set innerHTML(html: string) {
    const s = String(html ?? '');
    for (const c of [...this.childNodes]) {
      this.removeChild(c);
    }
    if (this.localName === 'style') {
      pendingOps.push({ op: 'addStyleText', id: this._id, css: s });
    } else {
      pendingOps.push({ op: 'setInnerHtml', id: this._id, html: s });
    }
  }
  get innerText() {
    return this.textContent;
  }
  set innerText(v: string) {
    this.textContent = v;
  }

  getBoundingClientRect() {
    const r = querySync({ op: 'getRect', id: this._id }) || {
      x: 0,
      y: 0,
      width: 0,
      height: 0,
      top: 0,
      left: 0,
      right: 0,
      bottom: 0,
    };
    return { ...r, toJSON: () => r };
  }
  getClientRects() {
    return [this.getBoundingClientRect()];
  }
  get offsetWidth() {
    return Math.round(this.getBoundingClientRect().width);
  }
  get offsetHeight() {
    return Math.round(this.getBoundingClientRect().height);
  }
  get offsetLeft() {
    return Math.round(this.getBoundingClientRect().left);
  }
  get offsetTop() {
    return Math.round(this.getBoundingClientRect().top);
  }
  get offsetParent() {
    return this.parentElement;
  }
  get clientWidth() {
    return this.offsetWidth || 1280;
  }
  get clientHeight() {
    return this.offsetHeight || 800;
  }
  get scrollWidth() {
    return this.clientWidth;
  }
  get scrollHeight() {
    return this.clientHeight;
  }
  scrollIntoView() {}
  scrollTo(x: any, y?: number) {
    if (typeof x === 'object' && x) {
      this.scrollLeft = x.left ?? this.scrollLeft;
      this.scrollTop = x.top ?? this.scrollTop;
    } else {
      this.scrollLeft = Number(x) || 0;
      this.scrollTop = Number(y) || 0;
    }
  }

  querySelector(selector: string): BlitzElement | null {
    const id = querySync({ op: 'querySelector', root: this._id, selector });
    return id ? ((nodeById.get(id) as BlitzElement) ?? null) : null;
  }
  querySelectorAll(selector: string): BlitzElement[] {
    const ids: number[] = querySync({ op: 'querySelectorAll', root: this._id, selector }) || [];
    return ids.map((id) => nodeById.get(id)).filter((n): n is BlitzElement => n instanceof BlitzElement);
  }
  matches(selector: string): boolean {
    return Boolean(querySync({ op: 'matches', id: this._id, selector }));
  }
  closest(selector: string): BlitzElement | null {
    const id = querySync({ op: 'closest', id: this._id, selector });
    return id ? ((nodeById.get(id) as BlitzElement) ?? null) : null;
  }
  getElementsByTagName(tag: string): BlitzElement[] {
    return this.querySelectorAll(tag);
  }
  getElementsByClassName(cls: string): BlitzElement[] {
    const sel = cls
      .trim()
      .split(/\s+/)
      .map((c) => '.' + c)
      .join('');
    return sel ? this.querySelectorAll(sel) : [];
  }

  focus() {
    const prev = documentObj.activeElement;
    if (prev && prev !== this) {
      prev.dispatchEvent(new BlitzFocusEvent('blur', { bubbles: false, relatedTarget: this }));
      prev.dispatchEvent(new BlitzFocusEvent('focusout', { bubbles: true, relatedTarget: this }));
    }
    documentObj.activeElement = this;
    pendingOps.push({ op: 'focus', id: this._id });
    this.dispatchEvent(new BlitzFocusEvent('focus', { bubbles: false, relatedTarget: prev }));
    this.dispatchEvent(new BlitzFocusEvent('focusin', { bubbles: true, relatedTarget: prev }));
  }
  blur() {
    if (documentObj.activeElement === this) {
      documentObj.activeElement = documentObj.body;
      pendingOps.push({ op: 'blur' });
      this.dispatchEvent(new BlitzFocusEvent('blur', { bubbles: false }));
      this.dispatchEvent(new BlitzFocusEvent('focusout', { bubbles: true }));
    }
  }
  click() {
    const r = this.getBoundingClientRect();
    const cx = r.left + r.width / 2;
    const cy = r.top + r.height / 2;
    this.dispatchEvent(new BlitzPointerEvent('pointerdown', { bubbles: true, cancelable: true, clientX: cx, clientY: cy, buttons: 1 }));
    this.dispatchEvent(new BlitzMouseEvent('mousedown', { bubbles: true, cancelable: true, clientX: cx, clientY: cy, buttons: 1 }));
    this.dispatchEvent(new BlitzPointerEvent('pointerup', { bubbles: true, cancelable: true, clientX: cx, clientY: cy, buttons: 0 }));
    this.dispatchEvent(new BlitzMouseEvent('mouseup', { bubbles: true, cancelable: true, clientX: cx, clientY: cy, buttons: 0 }));
    this.dispatchEvent(new BlitzMouseEvent('click', { bubbles: true, cancelable: true, clientX: cx, clientY: cy, buttons: 0 }));
  }
  toDataURL() {
    return 'data:image/png;base64,';
  }
  toBlob(cb: any) {
    if (typeof cb === 'function') cb(null);
  }
  setPointerCapture() {}
  releasePointerCapture() {}
  hasPointerCapture() {
    return false;
  }

  animate(keyframes: any, options: any = {}) {
    const id = nextAnimId++;
    const duration = typeof options === 'number' ? options : Number(options?.duration ?? 300);
    const delay = typeof options === 'object' ? Number(options?.delay ?? 0) : 0;
    const iterations = typeof options === 'object' ? Number(options?.iterations ?? 1) : 1;
    const easing = typeof options === 'object' ? String(options?.easing ?? 'ease') : 'ease';
    const fill = typeof options === 'object' ? String(options?.fill ?? 'none') : 'none';
    const frames: Record<string, string>[] = Array.isArray(keyframes)
      ? keyframes.map((k: any) => {
          const out: Record<string, string> = {};
          for (const [p, v] of Object.entries(k || {})) {
            if (p !== 'offset' && p !== 'easing' && p !== 'composite') out[p] = String(v);
          }
          return out;
        })
      : [];
    pendingOps.push({
      op: 'waapiStart',
      spec: {
        id,
        js_node_id: this._id,
        duration_ms: duration,
        delay_ms: delay,
        iterations: Number.isFinite(iterations) ? iterations : 999999,
        easing,
        fill,
        start_ms: simNowMs,
        frames,
      },
    });
    let resolveFinished: any;
    const finished = new Promise((r) => (resolveFinished = r));
    const anim = {
      id: String(id),
      playState: 'running',
      currentTime: 0,
      playbackRate: 1,
      finished,
      onfinish: null as any,
      oncancel: null as any,
      cancel() {
        anim.playState = 'idle';
        pendingOps.push({ op: 'waapiCancel', anim_id: id });
      },
      finish() {
        anim.playState = 'finished';
        resolveFinished(anim);
        anim.onfinish?.();
      },
      pause() {
        anim.playState = 'paused';
      },
      play() {
        anim.playState = 'running';
      },
      commitStyles() {},
      persist() {},
      addEventListener() {},
      removeEventListener() {},
    };
    return anim;
  }
  getAnimations() {
    return [];
  }

  getContext(type: string) {
    if (this.localName !== 'canvas') return null;
    if (type === '2d') {
      if (!this._ctx2d) {
        this._ctx2d = new BlitzCanvas2dContext(this);
        activeCanvas2ds.add(this._ctx2d);
      }
      return this._ctx2d;
    }
    if (type === 'webgl' || type === 'webgl2' || type === 'experimental-webgl') {
      if (!this._ctxGl) {
        this._ctxGl = createWebGlWgpuShim(this);
      }
      return this._ctxGl;
    }
    return null;
  }

  append(...nodes: any[]) {
    for (const n of nodes) {
      this.appendChild(typeof n === 'string' ? documentObj.createTextNode(n) : n);
    }
  }
  prepend(...nodes: any[]) {
    const first = this.firstChild;
    for (const n of nodes) {
      this.insertBefore(typeof n === 'string' ? documentObj.createTextNode(n) : n, first);
    }
  }
}

class BlitzCanvas2dContext {
  canvas: BlitzElement;
  fillStyle: any = '#ffffff';
  strokeStyle: any = '#ffffff';
  lineWidth = 1;
  globalAlpha = 1;
  font = '14px sans-serif';
  textAlign = 'start';
  textBaseline = 'alphabetic';
  lineCap = 'butt';
  lineJoin = 'miter';
  _ops: any[] = [];
  _path: any[] = [];
  _stack: any[] = [];
  _dirty = false;

  constructor(canvas: BlitzElement) {
    this.canvas = canvas;
  }
  _colorStr(c: any): string {
    if (typeof c === 'string') return c;
    if (c && c._stops && c._stops.length > 0) return c._stops[0][1];
    return '#ffffff';
  }
  clearRect(x: number, y: number, w: number, h: number) {
    this._ops = [{ op: 'clearRect', x, y, w, h }];
    this._dirty = true;
  }
  fillRect(x: number, y: number, w: number, h: number) {
    this._ops.push({ op: 'fillRect', x, y, w, h, color: this._colorStr(this.fillStyle), alpha: this.globalAlpha });
    this._dirty = true;
  }
  strokeRect(x: number, y: number, w: number, h: number) {
    this._ops.push({
      op: 'strokeRect',
      x,
      y,
      w,
      h,
      color: this._colorStr(this.strokeStyle),
      width: this.lineWidth,
      alpha: this.globalAlpha,
    });
    this._dirty = true;
  }
  beginPath() {
    this._path = [];
  }
  closePath() {
    this._path.push({ k: 'z' });
  }
  moveTo(x: number, y: number) {
    this._path.push({ k: 'm', x, y });
  }
  lineTo(x: number, y: number) {
    this._path.push({ k: 'l', x, y });
  }
  quadraticCurveTo(cx: number, cy: number, x: number, y: number) {
    this._path.push({ k: 'q', cx, cy, x, y });
  }
  bezierCurveTo(c1x: number, c1y: number, c2x: number, c2y: number, x: number, y: number) {
    this._path.push({ k: 'c', c1x, c1y, c2x, c2y, x, y });
  }
  arc(x: number, y: number, r: number, start: number, end: number, ccw = false) {
    this._path.push({ k: 'a', x, y, r, start, end, ccw });
  }
  rect(x: number, y: number, w: number, h: number) {
    this.moveTo(x, y);
    this.lineTo(x + w, y);
    this.lineTo(x + w, y + h);
    this.lineTo(x, y + h);
    this.closePath();
  }
  fill(pathOrRule?: any) {
    const cmds = pathOrRule && Array.isArray(pathOrRule._path) ? pathOrRule._path : this._path;
    if (cmds.length === 0) return;
    this._ops.push({
      op: 'fillPath',
      commands: cmds.slice(),
      color: this._colorStr(this.fillStyle),
      alpha: this.globalAlpha,
    });
    this._dirty = true;
  }
  stroke(pathObj?: any) {
    const cmds = pathObj && Array.isArray(pathObj._path) ? pathObj._path : this._path;
    if (cmds.length === 0) return;
    this._ops.push({
      op: 'strokePath',
      commands: cmds.slice(),
      color: this._colorStr(this.strokeStyle),
      width: this.lineWidth,
      alpha: this.globalAlpha,
    });
    this._dirty = true;
  }
  fillText(_text: string, x: number, y: number) {
    this._ops.push({
      op: 'fillRect',
      x,
      y: y - 10,
      w: Math.max(6, String(_text).length * 7),
      h: 10,
      color: this._colorStr(this.fillStyle),
      alpha: this.globalAlpha,
    });
    this._dirty = true;
  }
  measureText(t: string) {
    return { width: String(t).length * 8 };
  }
  save() {
    this._stack.push({ fillStyle: this.fillStyle, strokeStyle: this.strokeStyle, lineWidth: this.lineWidth, globalAlpha: this.globalAlpha });
  }
  restore() {
    const s = this._stack.pop();
    if (s) Object.assign(this, s);
    this._ops.push({ op: 'resetTransform' });
  }
  setTransform(a: number, b: number, c: number, d: number, e: number, f: number) {
    this._ops.push({ op: 'setTransform', a, b, c, d, e, f });
  }
  resetTransform() {
    this._ops.push({ op: 'resetTransform' });
  }
  translate(x: number, y: number) {
    this.setTransform(1, 0, 0, 1, x, y);
  }
  scale(x: number, y: number) {
    this.setTransform(x, 0, 0, y, 0, 0);
  }
  rotate(rad: number) {
    const c = Math.cos(rad);
    const s = Math.sin(rad);
    this.setTransform(c, s, -s, c, 0, 0);
  }
  createLinearGradient() {
    const g: any = { _stops: [], addColorStop(o: number, c: string) { g._stops.push([o, c]); } };
    return g;
  }
  createRadialGradient() {
    return this.createLinearGradient();
  }
  createImageData(w: number, h: number) {
    return { width: w, height: h, data: new Uint8ClampedArray(w * h * 4) };
  }
  putImageData() {}
  getImageData(x: number, y: number, w: number, h: number) {
    return this.createImageData(w, h);
  }
  _flushIfDirty() {
    if (!this._dirty) return;
    this._dirty = false;
    pendingOps.push({ op: 'canvas2dBatch', id: this.canvas._id, ops: this._ops.slice(-2000) });
  }
}

function createWebGlWgpuShim(canvas: BlitzElement) {
  let nextObj = 1;
  const base: Record<string, any> = {
    canvas,
    drawingBufferWidth: canvas.width,
    drawingBufferHeight: canvas.height,
    VERTEX_SHADER: 0x8b31,
    FRAGMENT_SHADER: 0x8b30,
    COMPILE_STATUS: 0x8b81,
    LINK_STATUS: 0x8b82,
    ACTIVE_UNIFORMS: 0x8b86,
    ACTIVE_ATTRIBUTES: 0x8b89,
    ARRAY_BUFFER: 0x8892,
    ELEMENT_ARRAY_BUFFER: 0x8893,
    STATIC_DRAW: 0x88e4,
    DYNAMIC_DRAW: 0x88e8,
    FLOAT: 0x1406,
    UNSIGNED_SHORT: 0x1403,
    UNSIGNED_INT: 0x1405,
    UNSIGNED_BYTE: 0x1401,
    TRIANGLES: 0x0004,
    TRIANGLE_STRIP: 0x0005,
    COLOR_BUFFER_BIT: 0x4000,
    DEPTH_BUFFER_BIT: 0x0100,
    BLEND: 0x0be2,
    DEPTH_TEST: 0x0b71,
    CULL_FACE: 0x0b44,
    SRC_ALPHA: 0x0302,
    ONE_MINUS_SRC_ALPHA: 0x0303,
    ONE: 1,
    ZERO: 0,
    TEXTURE_2D: 0x0de1,
    TEXTURE0: 0x84c0,
    RGBA: 0x1908,
    LINEAR: 0x2601,
    CLAMP_TO_EDGE: 0x812f,
    TEXTURE_MIN_FILTER: 0x2801,
    TEXTURE_MAG_FILTER: 0x2800,
    TEXTURE_WRAP_S: 0x2802,
    TEXTURE_WRAP_T: 0x2803,
    MAX_TEXTURE_SIZE: 0x0d33,
    MAX_VERTEX_ATTRIBS: 0x8869,
    MAX_COMBINED_TEXTURE_IMAGE_UNITS: 0x8b4d,
    VERSION: 0x1f02,
    VENDOR: 0x1f00,
    RENDERER: 0x1f01,
    SHADING_LANGUAGE_VERSION: 0x8b8c,
    getParameter(p: number) {
      if (p === 0x1f02) return 'WebGL 2.0 (Exo wgpu-DX12 shim)';
      if (p === 0x8b8c) return 'WebGL GLSL ES 3.00';
      if (p === 0x0d33) return 8192;
      if (p === 0x8869) return 16;
      return 16;
    },
    getExtension(name: string) {
      if (name === 'OES_vertex_array_object') {
        return {
          createVertexArrayOES: () => ({ id: nextObj++ }),
          bindVertexArrayOES: () => {},
          deleteVertexArrayOES: () => {},
        };
      }
      if (name === 'WEBGL_lose_context') return { loseContext() {}, restoreContext() {} };
      querySync({ op: 'webgl', cmd: 'unsupported', name: 'ext:' + name });
      return null;
    },
    getSupportedExtensions() {
      return ['OES_vertex_array_object', 'WEBGL_lose_context'];
    },
    getContextAttributes() {
      return { alpha: true, antialias: true, depth: true, premultipliedAlpha: true };
    },
    isContextLost() {
      return false;
    },
    createShader(type: number) {
      return { _id: Number(querySync({ op: 'webgl', cmd: 'createShader', type })) };
    },
    shaderSource(sh: any, src: string) {
      querySync({ op: 'webgl', cmd: 'shaderSource', id: sh?._id ?? 0, src: String(src) });
    },
    compileShader(sh: any) {
      sh._compileRes = querySync({ op: 'webgl', cmd: 'compileShader', id: sh?._id ?? 0 });
    },
    getShaderParameter(_sh: any, _p: number) {
      return true;
    },
    getShaderInfoLog(sh: any) {
      return sh?._compileRes?.error ?? '';
    },
    createProgram() {
      return { _id: Number(querySync({ op: 'webgl', cmd: 'createProgram' })) };
    },
    attachShader(prog: any, sh: any) {
      querySync({ op: 'webgl', cmd: 'attachShader', program: prog?._id ?? 0, shader: sh?._id ?? 0 });
    },
    linkProgram(prog: any) {
      prog._linked = Boolean(querySync({ op: 'webgl', cmd: 'linkProgram', program: prog?._id ?? 0 }));
    },
    getProgramParameter(_prog: any, p: number) {
      if (p === 0x8b82) return true;
      if (p === 0x8b86) return 0;
      if (p === 0x8b89) return 0;
      return true;
    },
    getProgramInfoLog() {
      return '';
    },
    useProgram(prog: any) {
      querySync({ op: 'webgl', cmd: 'useProgram', program: prog?._id ?? 0 });
    },
    getUniformLocation(_prog: any, name: string) {
      return { name };
    },
    getAttribLocation(_prog: any, _name: string) {
      return 0;
    },
    uniform1f(loc: any, v: number) {
      if (loc?.name) querySync({ op: 'webgl', cmd: 'uniform', name: loc.name, values: [v, 0, 0, 0] });
    },
    uniform2f(loc: any, a: number, b: number) {
      if (loc?.name) querySync({ op: 'webgl', cmd: 'uniform', name: loc.name, values: [a, b, 0, 0] });
    },
    uniform3f(loc: any, a: number, b: number, c: number) {
      if (loc?.name) querySync({ op: 'webgl', cmd: 'uniform', name: loc.name, values: [a, b, c, 0] });
    },
    uniform4f(loc: any, a: number, b: number, c: number, d: number) {
      if (loc?.name) querySync({ op: 'webgl', cmd: 'uniform', name: loc.name, values: [a, b, c, d] });
    },
    uniform1i(loc: any, v: number) {
      if (loc?.name) querySync({ op: 'webgl', cmd: 'uniform', name: loc.name, values: [v, 0, 0, 0] });
    },
    uniform2fv(loc: any, v: any) {
      if (loc?.name) querySync({ op: 'webgl', cmd: 'uniform', name: loc.name, values: [v[0] ?? 0, v[1] ?? 0, 0, 0] });
    },
    uniform3fv(loc: any, v: any) {
      if (loc?.name) querySync({ op: 'webgl', cmd: 'uniform', name: loc.name, values: [v[0] ?? 0, v[1] ?? 0, v[2] ?? 0, 0] });
    },
    uniform4fv(loc: any, v: any) {
      if (loc?.name) querySync({ op: 'webgl', cmd: 'uniform', name: loc.name, values: [v[0] ?? 0, v[1] ?? 0, v[2] ?? 0, v[3] ?? 0] });
    },
    uniformMatrix4fv() {},
    uniformMatrix3fv() {},
    createBuffer: () => ({ id: nextObj++ }),
    bindBuffer() {},
    bufferData() {},
    createVertexArray: () => ({ id: nextObj++ }),
    bindVertexArray() {},
    enableVertexAttribArray() {},
    disableVertexAttribArray() {},
    vertexAttribPointer() {},
    createTexture: () => ({ id: nextObj++ }),
    bindTexture() {},
    activeTexture() {},
    texParameteri() {},
    texImage2D() {},
    pixelStorei() {},
    viewport(_x: number, _y: number, w: number, h: number) {
      querySync({ op: 'webgl', cmd: 'viewport', w, h });
    },
    scissor() {},
    enable() {},
    disable() {},
    blendFunc() {},
    blendFuncSeparate() {},
    depthMask() {},
    cullFace() {},
    clearColor(r: number, g: number, b: number, a: number) {
      querySync({ op: 'webgl', cmd: 'clearColor', r, g, b, a });
    },
    clear() {},
    drawArrays() {
      querySync({ op: 'webgl', cmd: 'draw', canvasId: canvas._id });
    },
    drawElements() {
      querySync({ op: 'webgl', cmd: 'draw', canvasId: canvas._id });
    },
    deleteShader() {},
    deleteProgram() {},
    deleteBuffer() {},
    deleteTexture() {},
    getError: () => 0,
  };

  return new Proxy(base, {
    get(target, prop) {
      if (prop in target) return target[prop as string];
      if (typeof prop === 'string') {
        return (..._args: any[]) => {
          querySync({ op: 'webgl', cmd: 'unsupported', name: prop });
          return undefined;
        };
      }
      return undefined;
    },
  });
}

class BlitzResizeObserver {
  cb: Function;
  targets = new Set<BlitzElement>();
  constructor(cb: Function) {
    this.cb = cb;
    resizeObservers.add(this);
  }
  observe(el: BlitzElement) {
    this.targets.add(el);
    const r = el.getBoundingClientRect();
    queueMicrotask(() => {
      try {
        this.cb([{ target: el, contentRect: r, borderBoxSize: [{ inlineSize: r.width, blockSize: r.height }], contentBoxSize: [{ inlineSize: r.width, blockSize: r.height }] }], this);
      } catch (e: any) {
        __blitz_log('error', String(e?.stack || e));
      }
    });
  }
  unobserve(el: BlitzElement) {
    this.targets.delete(el);
  }
  disconnect() {
    this.targets.clear();
    resizeObservers.delete(this);
  }
}

class BlitzIntersectionObserver {
  cb: Function;
  targets = new Set<BlitzElement>();
  constructor(cb: Function) {
    this.cb = cb;
    intersectionObservers.add(this);
  }
  observe(el: BlitzElement) {
    this.targets.add(el);
    const r = el.getBoundingClientRect();
    queueMicrotask(() => {
      try {
        this.cb([{ target: el, isIntersecting: true, intersectionRatio: 1, boundingClientRect: r, intersectionRect: r, rootBounds: { x: 0, y: 0, width: 1280, height: 800 } }], this);
      } catch (e: any) {
        __blitz_log('error', String(e?.stack || e));
      }
    });
  }
  unobserve(el: BlitzElement) {
    this.targets.delete(el);
  }
  disconnect() {
    this.targets.clear();
    intersectionObservers.delete(this);
  }
  takeRecords() {
    return [];
  }
}

class BlitzMutationObserver {
  cb: Function;
  targets = new Set<BlitzNode>();
  records: any[] = [];
  constructor(cb: Function) {
    this.cb = cb;
    mutationObservers.add(this);
  }
  observe(target: BlitzNode) {
    this.targets.add(target);
  }
  disconnect() {
    this.targets.clear();
    mutationObservers.delete(this);
  }
  takeRecords() {
    return this.records.splice(0);
  }
}

function notifyMutation(target: BlitzNode, type: string, addedNodes: any[], removedNodes: any[], attributeName: string | null = null) {
  if (mutationObservers.size === 0) return;
  for (const obs of mutationObservers) {
    let hit = false;
    let cur: BlitzNode | null = target;
    while (cur) {
      if (obs.targets.has(cur)) {
        hit = true;
        break;
      }
      cur = cur.parentNode;
    }
    if (hit) {
      obs.records.push({ type, target, addedNodes, removedNodes, attributeName });
      queueMicrotask(() => {
        const recs = obs.takeRecords();
        if (recs.length > 0) {
          try {
            obs.cb(recs, obs);
          } catch {}
        }
      });
    }
  }
}

const documentObj: any = new BlitzEventTarget();
documentObj.nodeType = 9;
documentObj.nodeName = '#document';
documentObj.readyState = 'complete';
documentObj.compatMode = 'CSS1Compat';
documentObj.visibilityState = 'visible';
documentObj.hidden = false;
documentObj.defaultView = globalThis;
documentObj.styleSheets = [];
documentObj.fonts = { ready: Promise.resolve(), add() {}, check: () => true };

const htmlEl = new BlitzElement(1, 'html');
const headEl = new BlitzElement(2, 'head');
const bodyEl = new BlitzElement(3, 'body');
const rootEl = new BlitzElement(4, 'div');
rootEl._attrs.set('id', 'root');
htmlEl.childNodes.push(headEl, bodyEl);
headEl.parentNode = htmlEl;
bodyEl.parentNode = htmlEl;
bodyEl.childNodes.push(rootEl);
rootEl.parentNode = bodyEl;
htmlEl.parentNode = documentObj;
documentObj.documentElement = htmlEl;
documentObj.head = headEl;
documentObj.body = bodyEl;
documentObj.activeElement = bodyEl;
documentObj.childNodes = [htmlEl];

documentObj.matches = () => false;
documentObj.closest = () => null;
documentObj.createElement = (tag: string) => {
  const id = nextNodeId++;
  pendingOps.push({ op: 'createElement', id, tag: String(tag) });
  return new BlitzElement(id, String(tag));
};
documentObj.createElementNS = (nsUri: string, tag: string) => {
  const id = nextNodeId++;
  pendingOps.push({ op: 'createElement', id, tag: String(tag), ns: nsUri });
  return new BlitzElement(id, String(tag), nsUri);
};
documentObj.createTextNode = (text: string) => {
  const id = nextNodeId++;
  const s = String(text ?? '');
  pendingOps.push({ op: 'createText', id, text: s });
  return new BlitzText(id, s);
};
documentObj.createComment = (text: string) => {
  const id = nextNodeId++;
  const s = String(text ?? '');
  pendingOps.push({ op: 'createComment', id, text: s });
  return new BlitzComment(id, s);
};
documentObj.createDocumentFragment = () => new BlitzDocumentFragment();
documentObj.getElementById = (id: string) => {
  if (id === 'root') return rootEl;
  const hit = querySync({ op: 'querySelector', selector: '#' + id });
  return hit ? nodeById.get(hit) ?? null : null;
};
documentObj.querySelector = (selector: string) => {
  const hit = querySync({ op: 'querySelector', selector });
  return hit ? nodeById.get(hit) ?? null : null;
};
documentObj.querySelectorAll = (selector: string) => {
  const ids: number[] = querySync({ op: 'querySelectorAll', selector }) || [];
  return ids.map((id) => nodeById.get(id)).filter(Boolean);
};
documentObj.getElementsByTagName = (tag: string) => documentObj.querySelectorAll(tag);
documentObj.getElementsByClassName = (cls: string) => htmlEl.getElementsByClassName(cls);
documentObj.elementFromPoint = (x: number, y: number) => {
  const hit = querySync({ op: 'hitTest', x, y });
  return hit ? nodeById.get(hit) ?? null : null;
};
documentObj.elementsFromPoint = (x: number, y: number) => {
  const e = documentObj.elementFromPoint(x, y);
  return e ? [e] : [];
};
documentObj.contains = (n: BlitzNode | null) => htmlEl.contains(n);
documentObj.hasFocus = () => true;
documentObj.createEvent = (type: string) => new BlitzEvent(type);
documentObj.createRange = () => ({
  selectNodeContents() {},
  collapse() {},
  getBoundingClientRect: () => ({ x: 0, y: 0, width: 0, height: 0, top: 0, left: 0, right: 0, bottom: 0 }),
  getClientRects: () => [],
});
documentObj.getSelection = () => ({
  rangeCount: 0,
  removeAllRanges() {},
  addRange() {},
  toString: () => '',
});
documentObj.createTreeWalker = (root: BlitzNode, _whatToShow = 0xffffffff, filter?: any) => {
  const flat: BlitzNode[] = [];
  const walk = (n: BlitzNode) => {
    for (const c of n.childNodes) {
      let accept = 1;
      if (typeof filter === 'function') accept = filter(c);
      else if (filter && typeof filter.acceptNode === 'function') accept = filter.acceptNode(c);
      if (accept === 1) flat.push(c);
      walk(c);
    }
  };
  walk(root);
  let idx = -1;
  return {
    currentNode: root,
    nextNode() {
      idx++;
      if (idx < flat.length) {
        this.currentNode = flat[idx];
        return flat[idx];
      }
      return null;
    },
    previousNode() {
      idx--;
      if (idx >= 0) {
        this.currentNode = flat[idx];
        return flat[idx];
      }
      return null;
    },
  };
};

const windowObj: any = globalThis;
const winTarget = new BlitzEventTarget();
windowObj.window = windowObj;
windowObj.self = windowObj;
windowObj.top = windowObj;
windowObj.parent = windowObj;
windowObj.document = documentObj;
windowObj.innerWidth = 1280;
windowObj.innerHeight = 800;
windowObj.outerWidth = 1280;
windowObj.outerHeight = 800;
windowObj.devicePixelRatio = 1;
windowObj.scrollX = 0;
windowObj.scrollY = 0;
windowObj.pageXOffset = 0;
windowObj.pageYOffset = 0;
windowObj.screen = { width: 1280, height: 800, availWidth: 1280, availHeight: 800 };
windowObj.navigator = {
  userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) ExoBlitz/1.0',
  platform: 'Win32',
  language: 'en-US',
  languages: ['en-US'],
  maxTouchPoints: 0,
  hardwareConcurrency: 6,
};
windowObj.location = {
  href: 'https://exo.local/',
  origin: 'https://exo.local',
  protocol: 'https:',
  host: 'exo.local',
  hostname: 'exo.local',
  pathname: '/',
  search: '',
  hash: '',
};
windowObj.history = {
  length: 1,
  state: null,
  pushState() {},
  replaceState() {},
  back() {},
  forward() {},
};
windowObj.addEventListener = winTarget.addEventListener.bind(winTarget);
windowObj.removeEventListener = winTarget.removeEventListener.bind(winTarget);
windowObj.dispatchEvent = winTarget.dispatchEvent.bind(winTarget);
windowObj.scrollTo = () => {};
windowObj.scrollBy = () => {};
windowObj.matchMedia = (query: string) => {
  const matches = query.includes('dark') || query.includes('hover: hover') || query.includes('pointer: fine');
  return {
    matches,
    media: query,
    onchange: null,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
  };
};
windowObj.getComputedStyle = (el: BlitzElement) => {
  const cache = new Map<string, string>();
  const readProp = (prop: string) => {
    const k = camelToKebab(prop);
    if (!cache.has(k)) {
      const v = el?._id ? querySync({ op: 'getComputedStyle', id: el._id, prop: k }) : '';
      cache.set(k, typeof v === 'string' ? v : '');
    }
    return cache.get(k) || '';
  };
  return new Proxy(
    {
      getPropertyValue: (p: string) => readProp(p),
    },
    {
      get(target, prop) {
        if (typeof prop === 'symbol') return (target as any)[prop];
        if (prop in target) return (target as any)[prop];
        return readProp(String(prop));
      },
    },
  );
};
windowObj.CSS = {
  supports(a: string, b?: string) {
    const cond = b !== undefined ? `${a}: ${b}` : a;
    return Boolean(querySync({ op: 'cssSupports', cond }));
  },
  escape(s: string) {
    return String(s).replace(/([^\w-])/g, '\\$1');
  },
};
windowObj.performance = {
  now: () => simNowMs,
  mark() {},
  measure() {},
  timeOrigin: 0,
};
windowObj.requestAnimationFrame = (cb: (t: number) => void) => {
  const id = nextTimerId++;
  rafQueue.set(id, cb);
  return id;
};
windowObj.cancelAnimationFrame = (id: number) => {
  rafQueue.delete(id);
};
windowObj.setTimeout = (fn: Function, ms = 0, ...args: any[]) => {
  const id = nextTimerId++;
  timers.set(id, { fn, at: simNowMs + Math.max(0, Number(ms) || 0), interval: null, args });
  return id;
};
windowObj.clearTimeout = (id: number) => {
  timers.delete(id);
};
windowObj.setInterval = (fn: Function, ms = 16, ...args: any[]) => {
  const id = nextTimerId++;
  const iv = Math.max(16, Number(ms) || 16);
  timers.set(id, { fn, at: simNowMs + iv, interval: iv, args });
  return id;
};
windowObj.clearInterval = (id: number) => {
  timers.delete(id);
};
if (typeof windowObj.queueMicrotask !== 'function') {
  windowObj.queueMicrotask = (fn: Function) => Promise.resolve().then(() => fn());
}

windowObj.EventTarget = BlitzEventTarget;
windowObj.Event = BlitzEvent;
windowObj.CustomEvent = BlitzEvent;
windowObj.UIEvent = BlitzUIEvent;
windowObj.MouseEvent = BlitzMouseEvent;
windowObj.PointerEvent = BlitzPointerEvent;
windowObj.KeyboardEvent = BlitzKeyboardEvent;
windowObj.FocusEvent = BlitzFocusEvent;
windowObj.InputEvent = BlitzUIEvent;
windowObj.WheelEvent = BlitzMouseEvent;
windowObj.Node = BlitzNode;
windowObj.Text = BlitzText;
windowObj.Comment = BlitzComment;
windowObj.DocumentFragment = BlitzDocumentFragment;
windowObj.Element = BlitzElement;
windowObj.HTMLElement = BlitzElement;
windowObj.SVGElement = BlitzElement;
windowObj.HTMLInputElement = BlitzElement;
windowObj.HTMLTextAreaElement = BlitzElement;
windowObj.HTMLButtonElement = BlitzElement;
windowObj.HTMLSelectElement = BlitzElement;
windowObj.HTMLCanvasElement = BlitzElement;
windowObj.HTMLStyleElement = BlitzElement;
windowObj.HTMLDivElement = BlitzElement;
windowObj.HTMLSpanElement = BlitzElement;
windowObj.HTMLAnchorElement = BlitzElement;
windowObj.HTMLLabelElement = BlitzElement;
windowObj.HTMLFormElement = BlitzElement;
windowObj.HTMLOptionElement = BlitzElement;
windowObj.HTMLImageElement = BlitzElement;
windowObj.HTMLVideoElement = BlitzElement;
windowObj.HTMLAudioElement = BlitzElement;
windowObj.HTMLDialogElement = BlitzElement;
windowObj.HTMLIFrameElement = class HTMLIFrameElement extends BlitzElement {};
windowObj.ShadowRoot = class ShadowRoot extends BlitzDocumentFragment {};
windowObj.SVGSVGElement = BlitzElement;
windowObj.SVGPathElement = BlitzElement;
windowObj.SVGGraphicsElement = BlitzElement;
windowObj.CharacterData = BlitzText;
windowObj.CSSStyleDeclaration = class CSSStyleDeclaration {};
windowObj.CSSStyleSheet = class CSSStyleSheet {};
windowObj.DOMRect = class DOMRect {};
windowObj.DOMException = class DOMException extends Error {};
windowObj.CanvasRenderingContext2D = BlitzCanvas2dContext;
windowObj.WebGLRenderingContext = class WebGLRenderingContext {};
windowObj.WebGL2RenderingContext = class WebGL2RenderingContext {};
windowObj.OffscreenCanvas = class OffscreenCanvas {};
windowObj.Path2D = class Path2D {
  _path: any[] = [];
  moveTo(x: number, y: number) { this._path.push({ k: 'm', x, y }); }
  lineTo(x: number, y: number) { this._path.push({ k: 'l', x, y }); }
  arc(x: number, y: number, r: number, start: number, end: number, ccw = false) {
    this._path.push({ k: 'a', x, y, r, start, end, ccw });
  }
  rect(x: number, y: number, w: number, h: number) {
    this.moveTo(x, y); this.lineTo(x + w, y); this.lineTo(x + w, y + h); this.lineTo(x, y + h); this.closePath();
  }
  closePath() { this._path.push({ k: 'z' }); }
};
windowObj.Document = function Document() {};
windowObj.Window = function Window() {};
windowObj.ResizeObserver = BlitzResizeObserver;
windowObj.IntersectionObserver = BlitzIntersectionObserver;
windowObj.MutationObserver = BlitzMutationObserver;
windowObj.NodeFilter = { SHOW_ALL: 0xffffffff, SHOW_ELEMENT: 1, FILTER_ACCEPT: 1, FILTER_SKIP: 3 };
windowObj.Image = class {
  src = '';
  width = 0;
  height = 0;
  complete = true;
  onload: any = null;
  onerror: any = null;
};
windowObj.localStorage = (() => {
  const m = new Map<string, string>();
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => m.set(k, String(v)),
    removeItem: (k: string) => m.delete(k),
    clear: () => m.clear(),
  };
})();
windowObj.sessionStorage = windowObj.localStorage;
if (!windowObj.crypto) {
  let c = 1;
  windowObj.crypto = {
    randomUUID: () => `00000000-0000-4000-8000-${String(c++).padStart(12, '0')}`,
    getRandomValues: (arr: any) => {
      for (let i = 0; i < arr.length; i++) arr[i] = (Math.random() * 256) | 0;
      return arr;
    },
  };
}
if (!windowObj.Intl) {
  windowObj.Intl = {
    NumberFormat: class {
      format(n: number) { return String(n); }
      formatToParts(n: number) { return [{ type: 'integer', value: String(n) }]; }
      resolvedOptions() { return { locale: 'en-US', numberingSystem: 'latn', minimumFractionDigits: 0, maximumFractionDigits: 3 }; }
    },
    DateTimeFormat: class {
      format(d: any) { return String(d); }
      formatToParts(d: any) { return [{ type: 'literal', value: String(d) }]; }
      resolvedOptions() { return { locale: 'en-US', calendar: 'gregory', numberingSystem: 'latn', timeZone: 'UTC' }; }
    },
    Collator: class {
      compare(a: string, b: string) { return a < b ? -1 : a > b ? 1 : 0; }
      resolvedOptions() { return { locale: 'en-US' }; }
    },
    PluralRules: class {
      select(n: number) { return n === 1 ? 'one' : 'other'; }
    },
    ListFormat: class {
      format(arr: string[]) { return arr.join(', '); }
    },
  };
}
if (!windowObj.TextEncoder) {
  windowObj.TextEncoder = class {
    encode(s: string) {
      const out = new Uint8Array(s.length);
      for (let i = 0; i < s.length; i++) out[i] = s.charCodeAt(i) & 0xff;
      return out;
    }
  };
}
if (!windowObj.TextDecoder) {
  windowObj.TextDecoder = class {
    decode(buf: Uint8Array) {
      return String.fromCharCode(...buf);
    }
  };
}
if (!windowObj.console) {
  windowObj.console = {
    log: (...a: any[]) => __blitz_log('info', a.map(String).join(' ')),
    info: (...a: any[]) => __blitz_log('info', a.map(String).join(' ')),
    warn: (...a: any[]) => __blitz_log('warn', a.map(String).join(' ')),
    error: (...a: any[]) => __blitz_log('error', a.map(String).join(' ')),
    debug: () => {},
  };
} else {
  const origErr = windowObj.console.error?.bind(windowObj.console);
  windowObj.console.error = (...a: any[]) => {
    __blitz_log('error', a.map(String).join(' '));
    origErr?.(...a);
  };
}

windowObj.__blitz_flush = flushOps;
windowObj.__blitz_tick = (nowMs: number) => {
  simNowMs = nowMs;
  for (const [id, t] of [...timers.entries()]) {
    if (t.at <= simNowMs) {
      if (t.interval != null) {
        t.at = simNowMs + t.interval;
      } else {
        timers.delete(id);
      }
      try {
        t.fn(...t.args);
      } catch (e: any) {
        __blitz_log('error', String(e?.stack || e));
      }
    }
  }
  const cbs = [...rafQueue.entries()];
  rafQueue.clear();
  for (const [, cb] of cbs) {
    try {
      cb(simNowMs);
    } catch (e: any) {
      __blitz_log('error', String(e?.stack || e));
    }
  }
  flushOps();
};
