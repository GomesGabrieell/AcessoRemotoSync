import assert from "node:assert/strict";
import { test } from "node:test";
import { createHash } from "node:crypto";
import { setTimeout as esperar } from "node:timers/promises";
import { ArquivosWeb } from "../src/SyncDesk.Sinalizacao/wwwroot/arquivos-web.js";

const sha = (bytes) => createHash("sha256").update(bytes).digest("base64");
const idWindows = "a".repeat(32);

async function aguardar(condicao) {
  for (let i = 0; i < 100 && !condicao(); i++) await esperar(10);
  assert.ok(condicao());
}

test("a oferta web envia vários blocos com SHA-256 e aguarda a confirmação do Windows", async (t) => {
  const conteudo = Buffer.alloc(180_000, 0x24);
  const pacotes = [];
  const progresso = [];
  const blocos = [];
  const web = new ArquivosWeb((m) => {
    pacotes.push(m);
    const { id, valor } = m;
    if (m.tipo === "arquivo") web.receber({ tipo: "pronto", id, valor: 0 });
    if (m.tipo === "bloco") blocos.push(Buffer.from(m.dados, "base64"));
    if (m.tipo === "janela") web.receber({ tipo: "janela", id, valor });
    if (m.tipo === "fimArquivo") web.receber({ tipo: "gravado", id, valor: 0 });
    if (m.tipo === "fim") web.receber({ tipo: "concluido", id, valor: 1 });
  }, () => true, (p) => progresso.push(p), () => {}, () => {});
  t.after(() => web.fechar());
  web.selecionar([new File([conteudo], "teste.bin")]);
  web.receber({ tipo: "colar", id: idWindows, texto: pacotes[0].id });
  await aguardar(() => progresso.some((p) => /enviados e conferidos/.test(p.texto)));
  assert.deepEqual(Buffer.concat(blocos), conteudo);
  assert.equal(pacotes.find((m) => m.tipo === "fimArquivo").dados, sha(conteudo));
  assert.ok(pacotes.filter((m) => m.tipo === "janela").length > 1);
});

test("o navegador oferece download só depois de conferir o lote e rejeita um hash incorreto", async (t) => {
  const bytes = Buffer.from("arquivo de teste");
  const pacotes = [];
  const downloads = [];
  const web = new ArquivosWeb((m) => pacotes.push(m), () => true, () => {}, () => {},
    (lote) => downloads.push(...lote));
  t.after(() => web.fechar());
  web.receber({ tipo: "oferta", id: idWindows, valor: 2 });
  await esperar(10);
  for (const valido of [false, true]) {
    web.solicitar();
    const { id } = pacotes.at(-1);
    web.receber({ tipo: "lote", id, valor: 1, texto: String(bytes.length) });
    web.receber({ tipo: "arquivo", id, valor: bytes.length, texto: "teste.txt" });
    web.receber({ tipo: "bloco", id, valor: 0, dados: bytes.toString("base64") });
    web.receber({ tipo: "janela", id, valor: bytes.length });
    web.receber({ tipo: "fimArquivo", id, dados: sha(valido ? bytes : Buffer.from("incorreto")) });
    web.receber({ tipo: "fim", id });
    await aguardar(() => pacotes.some((m) => m.id === id && m.tipo === (valido ? "concluido" : "cancelar")));
    assert.equal(downloads.length, valido ? 1 : 0);
  }
  assert.deepEqual(Buffer.from(await downloads[0].blob.arrayBuffer()), bytes);
});
