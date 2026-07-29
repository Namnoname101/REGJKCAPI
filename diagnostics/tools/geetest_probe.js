const userAgent =
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";

const timers = new Set();

function makeElement(tagName) {
  const listeners = new Map();
  return {
    tagName: tagName.toUpperCase(),
    nodeName: tagName.toUpperCase(),
    style: {},
    dataset: {},
    children: [],
    parentNode: null,
    className: "",
    id: "",
    href: "",
    src: "",
    async: false,
    charset: "",
    crossOrigin: "",
    readyState: "",
    innerHTML: "",
    textContent: "",
    setAttribute(name, value) {
      this[name] = String(value);
      if (name === "id") this.id = String(value);
      if (name === "src") this.src = String(value);
      if (name === "href") this.href = String(value);
    },
    getAttribute(name) {
      return this[name];
    },
    appendChild(child) {
      child.parentNode = this;
      this.children.push(child);
      return child;
    },
    remove() {},
    addEventListener(name, fn) {
      listeners.set(name, fn);
    },
    removeEventListener(name) {
      listeners.delete(name);
    },
    dispatchEvent(event) {
      const fn = listeners.get(event.type);
      if (fn) fn.call(this, event);
    },
    getContext() {
      return {};
    },
    getBoundingClientRect() {
      return { width: 300, height: 60, top: 0, left: 0, right: 300, bottom: 60 };
    },
  };
}

async function evalScriptFromUrl(url, element) {
  const absolute = new URL(url, "https://static.botion.com").toString();
  console.error("script", absolute.slice(0, 240));
  const response = await fetch(absolute, {
    headers: {
      "user-agent": userAgent,
      referer: "https://www.rr3311.com/",
    },
  });
  if (!response.ok) {
    throw new Error(`script ${response.status}: ${absolute}`);
  }
  const source = await response.text();
  if (absolute.includes("/verify?")) {
    console.error("verify-response", source.slice(0, 2000));
  }
  (0, eval)(source);
  element.readyState = "complete";
  if (typeof element.onload === "function") element.onload();
  if (typeof element.onreadystatechange === "function") element.onreadystatechange();
}

const head = makeElement("head");
head.appendChild = function appendChild(child) {
  child.parentNode = this;
  this.children.push(child);
  if (child.tagName === "SCRIPT" && child.src) {
    evalScriptFromUrl(child.src, child).catch((error) => {
      console.error("script-error", error && error.message ? error.message : error);
      if (typeof child.onerror === "function") child.onerror(error);
    });
  } else {
    queueMicrotask(() => {
      if (typeof child.onload === "function") child.onload();
    });
  }
  return child;
};

const body = makeElement("body");
body.appendChild = head.appendChild.bind(body);

globalThis.window = globalThis;
globalThis.self = globalThis;
Object.defineProperty(globalThis, "location", {
  configurable: true,
  value: {
  protocol: "https:",
  host: "www.rr3311.com",
  hostname: "www.rr3311.com",
  href: "https://www.rr3311.com/register?referralCode=hug0888&affiliateCode=gioithieu",
  },
});
Object.defineProperty(globalThis, "navigator", {
  configurable: true,
  value: {
  userAgent,
  language: "vi-VN",
  userLanguage: "vi-VN",
  appName: "Netscape",
  platform: "Win32",
  },
});
globalThis.screen = { width: 1920, height: 1080, availWidth: 1920, availHeight: 1040 };
globalThis.document = {
  head,
  body,
  documentElement: { style: { fontSize: "16px" } },
  createElement: makeElement,
  getElementsByTagName(name) {
    if (name === "head") return [head];
    if (name === "body") return [body];
    return [];
  },
  getElementById() {
    return null;
  },
  querySelector() {
    return null;
  },
  querySelectorAll() {
    return [];
  },
  addEventListener() {},
  removeEventListener() {},
};
globalThis.MutationObserver = class {
  observe() {}
  disconnect() {}
};
globalThis.Image = class {};
globalThis.localStorage = new Map();
globalThis.sessionStorage = new Map();
for (const store of [globalThis.localStorage, globalThis.sessionStorage]) {
  store.getItem = store.get.bind(store);
  store.setItem = store.set.bind(store);
  store.removeItem = store.delete.bind(store);
}

const boc = await fetch("https://images.0998455.com/TCG_PROD_IMAGES/B2C/libs/gt/boc.js?v=1", {
  headers: { "user-agent": userAgent, referer: "https://www.rr3311.com/" },
}).then((response) => response.text());
(0, eval)(boc);

const timeout = setTimeout(() => {
  console.error("timeout");
  process.exit(2);
}, 15000);
timers.add(timeout);

await new Promise((resolve, reject) => {
  window.initBotion(
    {
      captchaId: "fa2202091f2d9086161947f672579b1d",
      product: "bind",
      language: "vie",
      hideSuccess: true,
      timeout: 5000,
      protocol: "https://",
    },
    (captcha) => {
      console.error("captcha-ready");
      captcha
        .onSuccess(() => {
          console.log(JSON.stringify(captcha.getValidate()));
          resolve();
        })
        .onError((error) => reject(error));
      if (captcha.onReady) {
        captcha.onReady(() => captcha.verify());
      } else {
        captcha.verify();
      }
    },
  );
});

clearTimeout(timeout);
