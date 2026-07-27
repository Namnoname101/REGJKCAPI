let source = "";

process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => {
  source += chunk;
});

process.stdin.on("end", () => {
  const start = source.indexOf("function Vr(");
  const end = source.indexOf("var Lh,Lm,Om,Oh;", start);
  if (start < 0 || end < 0) {
    throw new Error(`GG decoder block missing: start=${start}, end=${end}, length=${source.length}`);
  }

  const decoder = source.slice(start, end);
  const inspect = `
    function un(e, t) { return Vr(e - 220, t); }
    console.log(JSON.stringify({
      cryptoNamespace: un(410, "OmDt") + un(369, "bNV*"),
      encoderNamespace: un(367, "0vTc"),
      parseMethod: un(384, "bNV*"),
      iv: un(376, "jSy0") + un(436, "GHm#"),
      decodeMethod: un(351, "xt4n"),
      secret: un(428, "OmDt") + "BpZzRjbG91" + un(430, "@MPy"),
      salt: un(408, "PuJQ") + un(361, "GHm#") + "c0dac989c896",
      methods: {
        getSecret: un(434, "BXSQ"),
        getSalt: un(386, "7zH^"),
        hash: un(419, "TP4F"),
        encrypt: un(387, "$97Q"),
        decrypt: un(366, "TP4F"),
        ossDecrypt: un(398, "WoJK"),
        formEncrypt: un(390, "WoJK")
      },
      hashNames: {
        first: un(422, "$97Q"),
        stringify: un(424, "S9c)"),
        ghOuter: un(388, "!wuf"),
        ghInner: un(381, "ymqg"),
        substring: un(425, "teB["),
        fh: un(362, "681B")
      }
    }));
  `;

  (0, eval)(decoder + inspect);
});
