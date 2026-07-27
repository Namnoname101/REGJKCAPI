const nodeProcess = process;
const appId = nodeProcess.argv[2];
const siteOrigin = nodeProcess.argv[3] || "https://www.gg88v.com";
const registrationUrl = nodeProcess.argv[4] || `${siteOrigin}/home/register?id=456510325`;

if (!appId) {
  console.error("missing GeeGuard app id");
  nodeProcess.exit(2);
}

const mobileUserAgent =
  "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) " +
  "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 " +
  "Mobile/15E148 Safari/604.1";
const locationUrl = new URL(registrationUrl);

function shortError(error) {
  if (!error) return "unknown error";
  return error.stack || error.message || String(error).slice(0, 1000);
}

process.on("uncaughtException", (error) => {
  console.error(shortError(error));
  nodeProcess.exit(3);
});

process.on("unhandledRejection", (error) => {
  console.error(shortError(error));
  nodeProcess.exit(3);
});

class FakeEventTarget {
  constructor() {
    this.listeners = new Map();
  }

  addEventListener(name, callback) {
    if (typeof callback !== "function") return;
    const listeners = this.listeners.get(name) || [];
    listeners.push(callback);
    this.listeners.set(name, listeners);
  }

  removeEventListener(name, callback) {
    const listeners = this.listeners.get(name) || [];
    this.listeners.set(name, listeners.filter((listener) => listener !== callback));
  }

  dispatchEvent(event) {
    const type = typeof event === "string" ? event : event && event.type;
    for (const listener of this.listeners.get(type) || []) {
      listener.call(this, event);
    }
    return true;
  }
}

class FakeElement extends FakeEventTarget {
  constructor(tagName = "div") {
    super();
    this.tagName = String(tagName).toUpperCase();
    this.nodeName = this.tagName;
    this.nodeType = 1;
    this.style = {};
    this.dataset = {};
    this.children = [];
    this.childNodes = this.children;
    this.parentNode = null;
    this.attributes = {};
    this.className = "";
    this.id = "";
    this.href = "";
    this.src = "";
    this.async = false;
    this.charset = "";
    this.crossOrigin = "";
    this.readyState = "";
    this.innerHTML = "";
    this.textContent = "";
    this.offsetWidth = 390;
    this.offsetHeight = 844;
    this.clientWidth = 390;
    this.clientHeight = 844;
    this.width = 300;
    this.height = 150;
    this._contexts = new Map();
    this._canvasSeed = 2166136261;
  }

  setAttribute(name, value) {
    const text = String(value);
    this.attributes[name] = text;
    this[name] = text;
  }

  getAttribute(name) {
    return this.attributes[name] ?? this[name] ?? null;
  }

  appendChild(child) {
    child.parentNode = this;
    this.children.push(child);
    return child;
  }

  removeChild(child) {
    this.children = this.children.filter((item) => item !== child);
    this.childNodes = this.children;
    child.parentNode = null;
    return child;
  }

  remove() {
    if (this.parentNode && this.parentNode.removeChild) {
      this.parentNode.removeChild(this);
    }
  }

  getContext(type) {
    if (this._contexts.has(type)) return this._contexts.get(type);

    if (type === "2d") {
      const updateSeed = (...values) => {
        for (const value of values.join("|")) {
          this._canvasSeed ^= value.charCodeAt(0);
          this._canvasSeed = Math.imul(this._canvasSeed, 16777619) >>> 0;
        }
      };
      const context = new Proxy(
        {
          canvas: this,
          measureText: (text) => ({
            width: String(text).length * 7.421875,
            actualBoundingBoxLeft: 0,
            actualBoundingBoxRight: String(text).length * 7.2,
            actualBoundingBoxAscent: 10,
            actualBoundingBoxDescent: 3,
          }),
          getImageData: (_x, _y, width = 1, height = 1) => {
            const length = Math.max(4, Math.min(Number(width) * Number(height) * 4, 16384));
            const data = new Uint8ClampedArray(length);
            let state = this._canvasSeed;
            for (let index = 0; index < data.length; index += 4) {
              state ^= state << 13;
              state ^= state >>> 17;
              state ^= state << 5;
              data[index] = state & 0xff;
              data[index + 1] = (state >>> 8) & 0xff;
              data[index + 2] = (state >>> 16) & 0xff;
              data[index + 3] = 255;
            }
            return { data, width, height, colorSpace: "srgb" };
          },
        },
        {
          get(target, property) {
            if (property in target) return target[property];
            return (...args) => {
              updateSeed(property, ...args);
              return undefined;
            };
          },
          set(target, property, value) {
            updateSeed(property, value);
            target[property] = value;
            return true;
          },
        },
      );
      this._contexts.set(type, context);
      return context;
    }

    if (String(type).includes("webgl")) {
      const debugRenderer = {
        UNMASKED_VENDOR_WEBGL: 0x9245,
        UNMASKED_RENDERER_WEBGL: 0x9246,
      };
      const context = {
        VENDOR: 0x1f00,
        RENDERER: 0x1f01,
        VERSION: 0x1f02,
        SHADING_LANGUAGE_VERSION: 0x8b8c,
        getExtension: (name) => name === "WEBGL_debug_renderer_info" ? debugRenderer : null,
        getSupportedExtensions: () => [
          "EXT_blend_minmax",
          "EXT_color_buffer_half_float",
          "EXT_float_blend",
          "EXT_texture_filter_anisotropic",
          "OES_element_index_uint",
          "OES_standard_derivatives",
          "OES_texture_float",
          "OES_texture_half_float",
          "WEBGL_debug_renderer_info",
          "WEBGL_lose_context",
        ],
        getParameter: (parameter) => ({
          0x1f00: "WebKit",
          0x1f01: "WebKit WebGL",
          0x1f02: "WebGL 1.0 (OpenGL ES 2.0 Apple GPU)",
          0x8b8c: "WebGL GLSL ES 1.0",
          0x9245: "Apple Inc.",
          0x9246: "Apple GPU",
          0x0d33: 16384,
          0x851c: 16384,
          0x8872: 16,
          0x8b4d: 16,
          0x8b4c: 1024,
          0x8dfb: 1024,
          0x8dfd: 1024,
        })[parameter] ?? 0,
      };
      this._contexts.set(type, context);
      return context;
    }

    return null;
  }

  toDataURL() {
    return "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAF/gL+3fK4WQAAAABJRU5ErkJggg==";
  }

  getBoundingClientRect() {
    return { width: 390, height: 844, top: 0, left: 0, right: 390, bottom: 844 };
  }

  querySelector() {
    return null;
  }

  querySelectorAll() {
    return [];
  }
}

async function evaluateScript(url, element) {
  const response = await fetch(url, {
    headers: {
      "user-agent": mobileUserAgent,
      referer: registrationUrl,
    },
  });
  if (!response.ok) {
    throw new Error(`script ${response.status}: ${url}`);
  }

  const source = await response.text();
  (0, eval)(source);
  element.readyState = "complete";
  if (typeof element.onload === "function") element.onload();
  if (typeof element.onreadystatechange === "function") element.onreadystatechange();
  element.dispatchEvent({ type: "load" });
}

const head = new FakeElement("head");
const body = new FakeElement("body");
const documentElement = new FakeElement("html");
const originalAppendChild = FakeElement.prototype.appendChild;

function appendAndLoad(child) {
  originalAppendChild.call(this, child);
  if (child.tagName === "SCRIPT" && child.src) {
    evaluateScript(new URL(child.src, siteOrigin).toString(), child).catch((error) => {
      if (typeof child.onerror === "function") child.onerror(error);
      child.dispatchEvent({ type: "error", error });
    });
  }
  return child;
}

head.appendChild = appendAndLoad.bind(head);
body.appendChild = appendAndLoad.bind(body);

const cookieJar = new Map();
const documentObject = new FakeEventTarget();
Object.assign(documentObject, {
  head,
  body,
  documentElement,
  visibilityState: "visible",
  hidden: false,
  referrer: registrationUrl,
  URL: registrationUrl,
  domain: locationUrl.hostname,
  readyState: "complete",
  characterSet: "UTF-8",
  compatMode: "CSS1Compat",
  createElement: (tagName) => new FakeElement(tagName),
  createTextNode: (text) => ({ nodeName: "#text", nodeType: 3, textContent: String(text) }),
  getElementsByTagName(name) {
    if (name === "head") return [head];
    if (name === "body") return [body];
    if (name === "html") return [documentElement];
    return [];
  },
  getElementById: () => null,
  querySelector: () => null,
  querySelectorAll: () => [],
  hasFocus: () => true,
});

Object.defineProperty(documentObject, "cookie", {
  configurable: true,
  get() {
    return Array.from(cookieJar.entries()).map(([key, value]) => `${key}=${value}`).join("; ");
  },
  set(value) {
    const pair = String(value).split(";", 1)[0];
    const separator = pair.indexOf("=");
    if (separator > 0) cookieJar.set(pair.slice(0, separator), pair.slice(separator + 1));
  },
});

class FakeXmlHttpRequest extends FakeEventTarget {
  constructor() {
    super();
    this.headers = {};
    this.readyState = 0;
    this.status = 0;
    this.responseText = "";
    this.response = "";
    this.responseType = "";
    this.withCredentials = false;
  }

  open(method, url, async = true) {
    this.method = method;
    this.url = new URL(url, siteOrigin).toString();
    this.async = async;
    this.readyState = 1;
    this._notify("readystatechange");
  }

  setRequestHeader(name, value) {
    this.headers[name] = value;
  }

  getAllResponseHeaders() {
    return this.responseHeaders || "";
  }

  getResponseHeader(name) {
    return this.responseHeaderMap?.get(String(name).toLowerCase()) ?? null;
  }

  abort() {}

  send(body = null) {
    fetch(this.url, {
      method: this.method,
      headers: {
        ...this.headers,
        "user-agent": mobileUserAgent,
        referer: registrationUrl,
      },
      body: this.method === "GET" || this.method === "HEAD" ? undefined : body,
    })
      .then(async (response) => {
        this.status = response.status;
        this.statusText = response.statusText;
        this.responseHeaderMap = new Map();
        response.headers.forEach((value, name) => this.responseHeaderMap.set(name.toLowerCase(), value));
        this.responseHeaders = Array.from(this.responseHeaderMap.entries())
          .map(([name, value]) => `${name}: ${value}`)
          .join("\r\n");
        const text = await response.text();
        this.responseText = text;
        this.response = this.responseType === "json" ? JSON.parse(text) : text;
        this.readyState = 4;
        this._notify("readystatechange");
        this._notify("load");
        this._notify("loadend");
      })
      .catch((error) => {
        this.error = error;
        this.readyState = 4;
        this._notify("readystatechange");
        this._notify("error", error);
        this._notify("loadend");
      });
  }

  _notify(type, error) {
    const event = { type, target: this, error };
    const handler = this[`on${type}`];
    if (typeof handler === "function") handler.call(this, event);
    this.dispatchEvent(event);
  }
}

function createStorage() {
  const values = new Map();
  return {
    get length() {
      return values.size;
    },
    key: (index) => Array.from(values.keys())[index] ?? null,
    getItem: (key) => values.get(String(key)) ?? null,
    setItem: (key, value) => values.set(String(key), String(value)),
    removeItem: (key) => values.delete(String(key)),
    clear: () => values.clear(),
  };
}

globalThis.window = globalThis;
globalThis.self = globalThis;
globalThis.top = globalThis;
globalThis.parent = globalThis;
globalThis.name = "";
globalThis.document = documentObject;
globalThis.Element = FakeElement;
globalThis.HTMLElement = FakeElement;
globalThis.HTMLCanvasElement = FakeElement;
globalThis.XMLHttpRequest = FakeXmlHttpRequest;
globalThis.MutationObserver = class {
  observe() {}
  disconnect() {}
};
globalThis.Image = class extends FakeElement {
  constructor() {
    super("img");
  }
};
globalThis.localStorage = createStorage();
globalThis.sessionStorage = createStorage();
globalThis.screen = {
  width: 390,
  height: 844,
  availWidth: 390,
  availHeight: 844,
  colorDepth: 24,
  pixelDepth: 24,
  orientation: { type: "portrait-primary", angle: 0 },
};
globalThis.devicePixelRatio = 3;
globalThis.innerWidth = 390;
globalThis.innerHeight = 844;
globalThis.outerWidth = 390;
globalThis.outerHeight = 844;
globalThis.history = { length: 2, pushState() {}, replaceState() {} };
globalThis.chrome = undefined;
globalThis.WebGLRenderingContext = class {};
globalThis.requestAnimationFrame = (callback) => setTimeout(() => callback(Date.now()), 16);
globalThis.cancelAnimationFrame = clearTimeout;
globalThis.getComputedStyle = () => ({ getPropertyValue: () => "", display: "block" });
globalThis.open = () => null;
globalThis.addEventListener = documentObject.addEventListener.bind(documentObject);
globalThis.removeEventListener = documentObject.removeEventListener.bind(documentObject);
globalThis.dispatchEvent = documentObject.dispatchEvent.bind(documentObject);

Object.defineProperty(globalThis, "location", {
  configurable: true,
  value: {
    protocol: locationUrl.protocol,
    host: locationUrl.host,
    hostname: locationUrl.hostname,
    port: locationUrl.port,
    origin: locationUrl.origin,
    href: registrationUrl,
    pathname: locationUrl.pathname,
    search: locationUrl.search,
    hash: locationUrl.hash,
    ancestorOrigins: [],
    toString: () => registrationUrl,
  },
});

Object.defineProperty(globalThis, "navigator", {
  configurable: true,
  value: {
    userAgent: mobileUserAgent,
    appVersion: mobileUserAgent,
    language: "vi-VN",
    languages: ["vi-VN", "vi", "en-US"],
    userLanguage: "vi-VN",
    appName: "Netscape",
    appCodeName: "Mozilla",
    platform: "iPhone",
    product: "Gecko",
    vendor: "Apple Computer, Inc.",
    vendorSub: "",
    productSub: "20030107",
    cookieEnabled: true,
    onLine: true,
    hardwareConcurrency: 6,
    deviceMemory: 8,
    maxTouchPoints: 5,
    webdriver: false,
    plugins: [],
    mimeTypes: [],
    connection: { effectiveType: "4g", downlink: 10, rtt: 50, saveData: false },
    permissions: { query: async () => ({ state: "prompt" }) },
    mediaDevices: { enumerateDevices: async () => [] },
  },
});

Object.defineProperty(globalThis, Symbol.toStringTag, {
  configurable: true,
  value: "Window",
});
for (const name of ["require", "module", "exports", "global"]) {
  try {
    Object.defineProperty(globalThis, name, {
      configurable: true,
      value: undefined,
      writable: false,
    });
  } catch {}
}

setTimeout(() => {
  console.error("GeeGuard token timeout");
  nodeProcess.exit(4);
}, 15000).unref();

const bootstrapUrl = new URL("/libs/rc/gt@5/gt.js", siteOrigin).toString();
const bootstrap = await fetch(bootstrapUrl, {
  headers: {
    "user-agent": mobileUserAgent,
    referer: registrationUrl,
  },
}).then((response) => {
  if (!response.ok) throw new Error(`GeeGuard bootstrap ${response.status}`);
  return response.text();
});

(0, eval)(bootstrap);

window.initGeeGuard({ appId }, (result) => {
  const token = result && result.data && result.data.gee_token;
  if (!token) {
    console.error(`GeeGuard failed: ${JSON.stringify(result).slice(0, 1000)}`);
    nodeProcess.exit(5);
  }

  console.log(JSON.stringify({ token }));
  nodeProcess.exit(0);
});
