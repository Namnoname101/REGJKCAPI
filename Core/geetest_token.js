const captchaId = process.argv[2];
const sdkCdn = process.argv[3] || "https://images.0998455.com/";
const sdkVersion = process.argv[4] || "1";
const siteOrigin = process.argv[5] || "https://gioithieu.rr3311.com";
const registrationUrl =
  process.argv[6] || `${siteOrigin.replace(/\/$/, "")}/m/register?referralCode=mrh6472`;
const userAgent =
  process.argv[7] ||
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";

if (!captchaId) {
  console.error("missing captcha id");
  process.exit(2);
}

function shortError(error) {
  if (!error) return "unknown error";
  return error.message ? String(error.message) : String(error).slice(0, 500);
}

process.on("uncaughtException", (error) => {
  console.error(shortError(error));
  process.exit(3);
});

process.on("unhandledRejection", (error) => {
  console.error(shortError(error));
  process.exit(3);
});

const locationUrl = new URL(registrationUrl);
const mobileFingerprint = /iPhone|iPad|Android|Mobile/i.test(userAgent);

function makeElement(tagName) {
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
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent() {},
    getContext() {
      return {};
    },
    getBoundingClientRect() {
      return { width: 300, height: 60, top: 0, left: 0, right: 300, bottom: 60 };
    },
  };
}

function parseJsonp(source) {
  const start = source.indexOf("(");
  const end = source.lastIndexOf(")");
  if (start < 0 || end <= start) return null;
  return JSON.parse(source.slice(start + 1, end));
}

async function evalScriptFromUrl(url, element) {
  const absolute = new URL(url, "https://static.botion.com").toString();
  const response = await fetch(absolute, {
    headers: {
      "user-agent": userAgent,
      referer: registrationUrl,
    },
  });
  if (!response.ok) {
    throw new Error(`script ${response.status}: ${absolute}`);
  }

  const source = await response.text();
  if (absolute.includes("/verify?")) {
    const json = parseJsonp(source);
    if (json && json.status === "success" && json.data && json.data.seccode) {
      console.log(JSON.stringify(json.data.seccode));
      process.exit(0);
    }

    throw new Error(`geetest verify failed: ${source.slice(0, 300)}`);
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
      console.error(shortError(error));
      process.exit(3);
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
    protocol: locationUrl.protocol,
    host: locationUrl.host,
    hostname: locationUrl.hostname,
    href: registrationUrl,
  },
});
Object.defineProperty(globalThis, "navigator", {
  configurable: true,
  value: {
    userAgent,
    language: "vi-VN",
    userLanguage: "vi-VN",
    appName: "Netscape",
    platform: mobileFingerprint ? "iPhone" : "Win32",
    maxTouchPoints: mobileFingerprint ? 5 : 0,
    hardwareConcurrency: 8,
    deviceMemory: 8,
  },
});
const screenWidth = mobileFingerprint ? 390 : 1920;
const screenHeight = mobileFingerprint ? 844 : 1080;
globalThis.screen = {
  width: screenWidth,
  height: screenHeight,
  availWidth: screenWidth,
  availHeight: mobileFingerprint ? 844 : 1040,
  colorDepth: 24,
  pixelDepth: 24,
};
globalThis.innerWidth = screenWidth;
globalThis.innerHeight = screenHeight;
globalThis.outerWidth = screenWidth;
globalThis.outerHeight = screenHeight;
globalThis.devicePixelRatio = mobileFingerprint ? 3 : 1;
globalThis.document = {
  head,
  body,
  documentElement: { style: { fontSize: "16px" } },
  createElement: makeElement,
  createTextNode(text) {
    return { nodeName: "#text", textContent: String(text), remove() {} };
  },
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

for (const name of ["localStorage", "sessionStorage"]) {
  const map = new Map();
  map.getItem = (key) => map.get(key) || null;
  map.setItem = (key, value) => map.set(key, String(value));
  map.removeItem = (key) => map.delete(key);
  Object.defineProperty(globalThis, name, { configurable: true, value: map });
}

setTimeout(() => {
  console.error("geetest token timeout");
  process.exit(4);
}, 15000);

const bocUrl = new URL(`TCG_PROD_IMAGES/B2C/libs/gt/boc.js?v=${encodeURIComponent(sdkVersion)}`, sdkCdn).toString();
const riskUrl = new URL(`TCG_PROD_IMAGES/B2C/libs/g5/gd.js?v=${encodeURIComponent(sdkVersion)}`, sdkCdn).toString();
try {
  const risk = await fetch(riskUrl, {
    headers: {
      "user-agent": userAgent,
      referer: registrationUrl,
    },
  }).then((response) => {
    if (!response.ok) throw new Error(`risk ${response.status}`);
    return response.text();
  });
  (0, eval)(risk);
} catch {
  // The captcha token path can still succeed when GeeGuard is disabled.
}

const boc = await fetch(bocUrl, {
  headers: {
    "user-agent": userAgent,
    referer: registrationUrl,
  },
}).then((response) => {
  if (!response.ok) throw new Error(`boc ${response.status}`);
  return response.text();
});

(0, eval)(boc);

window.initBotion(
  {
    captchaId,
    product: "bind",
    language: "vie",
    hideSuccess: true,
    timeout: 5000,
    protocol: "https://",
  },
  (captcha) => {
    if (captcha.onReady) {
      captcha.onReady(() => captcha.verify());
    } else {
      captcha.verify();
    }
  },
);
