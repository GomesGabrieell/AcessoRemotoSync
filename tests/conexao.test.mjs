import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import path from "node:path";
import os from "node:os";
import { fileURLToPath } from "node:url";
import { createServer } from "node:net";
import { test } from "node:test";
import { chromium } from "playwright-core";

const servidor = fileURLToPath(new URL("../src/SyncDesk.Sinalizacao/", import.meta.url));
const credencial = randomBytes(32).toString("base64url");
const credencialRede = randomBytes(32).toString("base64url");
const credencialLimite = randomBytes(32).toString("base64url");
const nome = "Equipe SyncDesk";

async function portaLivre() {
  const socket = createServer();
  await new Promise((resolve, reject) => socket.once("error", reject).listen(0, "127.0.0.1", resolve));
  const porta = socket.address().port;
  await new Promise((resolve, reject) => socket.close((erro) => erro ? reject(erro) : resolve()));
  return porta;
}

async function status(pagina, texto, timeout = 12_000) {
  try {
    await pagina.getByRole("status").filter({ hasText: texto }).waitFor({ timeout });
  } catch (erro) {
    throw new Error(`Esperado: "${texto}". Exibido: "${await pagina.locator("#status").textContent()}".`, { cause: erro });
  }
}

async function par(contexto, url) {
  const cliente = await contexto.newPage();
  const tecnico = await contexto.newPage();
  await Promise.all([cliente.goto(url), tecnico.goto(url + "/?modo=tecnico")]);
  try {
    await status(cliente, "Aguardando conexão");
    await status(tecnico, "Aguardando código");
  } catch (erro) {
    const estados = await Promise.all([cliente.locator("#status").textContent(), tecnico.locator("#status").textContent()]);
    await Promise.all([cliente.close(), tecnico.close()]);
    throw new Error(estados.join(" / "), { cause: erro });
  }
  return { cliente, tecnico };
}

async function codigo(cliente) {
  return (await cliente.locator("#codigo").textContent()).trim();
}

async function solicitar(tecnico, valor, chave = credencial) {
  await tecnico.getByLabel("Código do cliente").fill(valor);
  await tecnico.getByLabel("Credencial do técnico").fill(chave);
  await tecnico.getByRole("button", { name: "Conectar", exact: true }).click();
}

test("sinalização HTTPS e WebRTC", { timeout: 90_000 }, async (t) => {
  const url = "https://localhost:" + await portaLivre();
  const prazoCodigo = 8;
  const temporario = await mkdtemp(path.join(os.tmpdir(), "syncdesk-teste-"));
  const processo = spawn(
    process.env.DOTNET_EXE || "dotnet",
    [path.join(servidor, "bin/Release/net10.0/SyncDesk.Sinalizacao.dll"), "--urls", url],
    {
      cwd: servidor,
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: "Development",
        ["SyncDesk__Tecnicos__" + nome]: credencial,
        SyncDesk__Tecnicos__Rede: credencialRede,
        SyncDesk__Tecnicos__Limite: credencialLimite,
        SyncDesk__ValidadeCodigoSegundos: String(prazoCodigo),
        SyncDesk__ArquivoCodigos: path.join(temporario, "codigos.bin"),
      },
      stdio: ["ignore", "pipe", "pipe"],
    },
  );
  const terminou = new Promise((resolve) => processo.once("close", resolve));
  t.after(async () => {
    processo.kill();
    await terminou;
    await rm(temporario, { recursive: true, force: true });
  });
  let saida = "";
  processo.stdout.on("data", (dados) => { saida += dados; });
  processo.stderr.on("data", (dados) => { saida += dados; });
  await new Promise((resolve, reject) => {
    const prazo = setTimeout(() => reject(new Error("O servidor não iniciou.\n" + saida)), 15_000);
    processo.once("error", (erro) => { clearTimeout(prazo); reject(erro); });
    processo.once("exit", () => { clearTimeout(prazo); reject(new Error(saida)); });
    processo.stdout.on("data", () => {
      if (saida.includes("Now listening on:")) {
        clearTimeout(prazo);
        resolve();
      }
    });
  });

  const navegador = await chromium.launch({
    executablePath: process.env.CHROMIUM_EXE || undefined,
    args: ["--ignore-certificate-errors", "--autoplay-policy=no-user-gesture-required"],
  });
  t.after(() => navegador.close());
  const contexto = await navegador.newContext({ ignoreHTTPSErrors: true });
  contexto.on("page", (pagina) => {
    pagina.on("pageerror", (erro) => t.diagnostic(erro.message));
    pagina.on("console", (linha) => { if (linha.type() === "error") t.diagnostic(linha.text()); });
    if (process.env.SYNC_DESK_TEST_TRACE) pagina.on("websocket", (socket) => {
      socket.on("framesent", ({ payload }) => t.diagnostic("enviado: " + JSON.parse(payload).tipo));
      socket.on("framereceived", ({ payload }) => t.diagnostic("recebido: " + JSON.parse(payload).tipo));
      socket.on("socketerror", (erro) => t.diagnostic(erro));
    });
  });
  // Substitui o seletor interativo por uma fonte determinística só no navegador do teste.
  // Os quadros e mensagens percorrem as conexões WebRTC reais do aplicativo.
  await contexto.addInitScript(() => {
    if (!navigator.mediaDevices) return;
    navigator.mediaDevices.getDisplayMedia = async () => {
      const canvas = document.createElement("canvas");
      canvas.width = 320;
      canvas.height = 200;
      const pincel = canvas.getContext("2d");
      let quadro = 0;
      const desenhar = () => {
        pincel.fillStyle = quadro++ % 2 ? "#1554d1" : "#ffffff";
        pincel.fillRect(0, 0, 320, 200);
      };
      desenhar();
      setInterval(desenhar, 50);
      return canvas.captureStream(20);
    };
  });

  // Registra no lugar da ponte WPF para verificar o tráfego sem injetar entrada no Windows do teste.
  await contexto.addInitScript(() => {
    window.syncDeskServidor = "wss://" + location.host;
    const SocketOriginal = window.WebSocket;
    window.WebSocket = class extends SocketOriginal {
      constructor(...argumentos) {
        super(...argumentos);
        window.sinalizacaoNoTeste = this;
        this.addEventListener("message", ({ data }) => {
          const mensagem = JSON.parse(data);
          if (mensagem.tipo === "sinal") (window.sinaisNoTeste ??= []).push(mensagem);
          if (mensagem.tipo === "solicitacao") window.atendimentoNoTeste = mensagem.atendimento;
        });
      }
      send(dados) {
        if (window.bloquearRenovacaoNoTeste && JSON.parse(dados).tipo === "renovar") return;
        return super.send(dados);
      }
    };
    window.entradasDoTeste = [];
    window.arquivosDoTeste = [];
    window.arquivosAtivosNoTeste = false;
    window.revogacoesNoTeste = 0;
    const ponte = new EventTarget();
    const nativa = window.syncDeskCapturaNativa = location.hash === "#captura-nativa";
    let geracao = 1;
    let telas = [["principal", 1]];
    let selecionado = "principal";
    const responder = (data) => ponte.dispatchEvent(new MessageEvent("message", { data }));
    const anunciarMonitores = () => responder({ tipo: "monitores", selecionado, geracao,
      telas: telas.map(([id, numero]) => ({ id, numero, autorizado: true })) });
    window.conectarMonitorNoTeste = () => {
      telas = [["principal", 1], ["secundario", 2]];
      anunciarMonitores();
    };
    window.geracoesDoTeste = [];
    window.enviarArquivoNoTeste = (mensagem) => ponte.dispatchEvent(new MessageEvent("message", { data: {
      tipo: "arquivo", mensagem,
    } }));
    ponte.postMessage = (dados) => {
      if (nativa && dados.tipo === "atendimentoNativo") {
        responder({ tipo: "permissao", controle: dados.controle, computador: "Cliente com monitores" });
        anunciarMonitores();
      }
      if (nativa && dados.tipo === "capturar") {
        const canvas = document.createElement("canvas");
        canvas.width = selecionado === "principal" ? 320 : 400;
        canvas.height = selecionado === "principal" ? 200 : 300;
        const pincel = canvas.getContext("2d");
        pincel.fillStyle = selecionado === "principal" ? "#1a60af" : "#12754b";
        pincel.fillRect(0, 0, canvas.width, canvas.height);
        responder({ tipo: "quadro", geracao, jpeg: canvas.toDataURL("image/jpeg").split(",")[1] });
      }
      if (nativa && dados.tipo === "selecionarMonitor") {
        selecionado = dados.id;
        geracao++;
        anunciarMonitores();
      }
      if (dados.tipo === "atendimento") {
        ponte.dispatchEvent(new MessageEvent("message", { data: {
          tipo: "permissao", controle: true, computador: "Cliente de teste",
        } }));
      }
      if (dados.tipo === "entrada") {
        window.entradasDoTeste.push(dados.evento);
        if (dados.evento.acao === "botao") window.geracoesDoTeste.push(dados.geracao);
      }
      if (dados.tipo === "arquivosAtivos") window.arquivosAtivosNoTeste = true;
      if (dados.tipo === "fim") window.revogacoesNoTeste++;
      if (dados.tipo === "arquivo") window.arquivosDoTeste.push(dados.mensagem);
    };
    window.chrome ??= {};
    window.chrome.webview = ponte;
  });

  await t.test("aceita, transporta vídeo e entrada junto com arquivos e encerra pelo cliente", async () => {
    const { cliente, tecnico } = await par(contexto, url);
    try {
      await tecnico.emulateMedia({ colorScheme: "light" });
      await tecnico.getByRole("button", { name: "Alternar tema" }).click();
      assert.equal(await tecnico.locator("html").getAttribute("data-tema"), "escuro");
      await tecnico.getByRole("button", { name: "Alternar tema" }).click();
      if (process.env.SYNC_DESK_CAPTURAS) {
        await cliente.setViewportSize({ width: 600, height: 560 });
        await tecnico.setViewportSize({ width: 900, height: 650 });
        await cliente.screenshot({ path: path.join(process.env.SYNC_DESK_CAPTURAS, "cliente.png") });
        await tecnico.screenshot({ path: path.join(process.env.SYNC_DESK_CAPTURAS, "tecnico.png") });
      }
      await solicitar(tecnico, await codigo(cliente));
      await status(tecnico, "Aguardando autorização");
      assert.equal(await tecnico.getByLabel("Credencial do técnico").inputValue(), "");
      assert.equal(await cliente.locator("#identidade").textContent(), "Técnico: " + nome);
      assert.equal(await tecnico.locator("#tela").isVisible(), false);
      await cliente.evaluate(() => window.sinalizacaoNoTeste.send(JSON.stringify({
        tipo: "sinal", sessao: window.atendimentoNoTeste, dados: { descricao: { type: "offer", sdp: "v=0" } }
      })));
      assert.deepEqual(await tecnico.evaluate(() => window.sinaisNoTeste ?? []), []);
      await cliente.getByRole("button", { name: "Autorizar acesso" }).click();
      await Promise.all([status(cliente, "Conexão WebRTC estabelecida"), status(tecnico, "Conexão WebRTC estabelecida")]);
      assert.equal(await tecnico.evaluate(() => (window.sinaisNoTeste ?? [])
        .some((sinal) => sinal.dados.descricao?.sdp === "v=0")), false);
      await tecnico.waitForFunction(() => {
        const video = document.querySelector("video");
        return video.videoWidth === 320 && video.currentTime > 0;
      });
      await tecnico.getByText("Cliente de teste", { exact: true }).waitFor();
      await tecnico.getByText("Qualidade automática", { exact: true }).waitFor();
      assert.equal((await tecnico.locator(".barra-sessao").boundingBox()).height, 32);
      await tecnico.getByText("Ações", { exact: true }).click();
      await tecnico.getByRole("button", { name: "Tamanho original" }).click();
      assert.equal(await tecnico.locator("#viewport-video").getAttribute("data-original"), "true");
      await tecnico.getByRole("button", { name: "Ajustar à janela" }).click();
      await tecnico.getByText("Ações", { exact: true }).click();
      await tecnico.getByRole("button", { name: "Tela cheia" }).click();
      await tecnico.waitForFunction(() => Boolean(document.fullscreenElement));
      await tecnico.getByRole("button", { name: "Sair da tela cheia" }).click();
      assert.equal(await cliente.getByText("Atendimento ativo", { exact: true }).isVisible(), true);
      await tecnico.getByRole("button", { name: "Chat", exact: true }).click();
      await tecnico.getByRole("textbox", { name: "Mensagem" }).fill("Olá, cliente");
      await tecnico.getByRole("button", { name: "Enviar", exact: true }).click();
      await cliente.getByText("Técnico · Olá, cliente", { exact: true }).waitFor();
      await cliente.getByRole("textbox", { name: "Mensagem" }).fill("Olá, técnico");
      await cliente.getByRole("button", { name: "Enviar", exact: true }).click();
      await tecnico.getByText("Cliente · Olá, técnico", { exact: true }).waitFor();
      await tecnico.getByRole("button", { name: "Chat", exact: true }).click();
      const video = await tecnico.locator("#tela").boundingBox();
      await tecnico.mouse.click(video.x + video.width / 2, video.y + video.height / 2);
      await tecnico.keyboard.press("a");
      await tecnico.mouse.wheel(0, 120);
      await cliente.waitForFunction(() => {
        const entradas = window.entradasDoTeste;
        return entradas.some((x) => x.acao === "botao" && x.pressionar && Math.abs(x.x - .5) < .01 && Math.abs(x.y - .5) < .01) &&
          entradas.some((x) => x.acao === "botao" && !x.pressionar) &&
          entradas.some((x) => x.acao === "tecla" && x.codigo === "KeyA" && x.pressionar) &&
          entradas.some((x) => x.acao === "tecla" && x.codigo === "KeyA" && !x.pressionar) &&
          entradas.some((x) => x.acao === "rolar" && x.dy > 0);
      });
      await Promise.all([cliente.waitForFunction(() => window.arquivosAtivosNoTeste),
        tecnico.waitForFunction(() => window.arquivosAtivosNoTeste)]);
      await tecnico.getByRole("button", { name: "Arquivos", exact: true }).click();
      assert.equal(await tecnico.getByRole("heading", { name: "Transferência de arquivos" }).isVisible(), true);
      await tecnico.getByRole("button", { name: "Arquivos", exact: true }).click();
      await tecnico.evaluate(() => window.enviarArquivoNoTeste({ tipo: "bloco", dados: "AQID" }));
      await cliente.evaluate(() => window.enviarArquivoNoTeste({ tipo: "bloco", dados: "BAUG" }));
      await cliente.waitForFunction(() => window.arquivosDoTeste.some((x) => x.dados === "AQID"));
      await tecnico.waitForFunction(() => window.arquivosDoTeste.some((x) => x.dados === "BAUG"));
      await tecnico.evaluate(() => {
        const dados = "A".repeat(15020);
        for (let indice = 0; indice < 12; indice++) window.enviarArquivoNoTeste({ tipo: "bloco", dados, indice });
      });
      await tecnico.locator("#tela").focus();
      await tecnico.keyboard.press("b");
      await cliente.waitForFunction(() => window.arquivosDoTeste.filter((x) => Number.isInteger(x.indice)).length === 12 &&
        window.entradasDoTeste.some((x) => x.acao === "tecla" && x.codigo === "KeyB"));
      const anterior = await codigo(cliente);
      const cliques = await cliente.evaluate(() => window.entradasDoTeste.filter((x) => x.acao === "botao").length);
      await cliente.getByRole("button", { name: "Encerrar atendimento", exact: true }).click();
      await status(tecnico, "O cliente encerrou o atendimento.");
      assert.equal(await tecnico.locator("#tela").isVisible(), false);
      assert.equal(await cliente.evaluate(() => window.revogacoesNoTeste), 1);
      await tecnico.mouse.click(video.x + video.width / 2, video.y + video.height / 2);
      assert.equal(await cliente.evaluate(() => window.entradasDoTeste.filter((x) => x.acao === "botao").length), cliques);
      await status(cliente, "Aguardando conexão");
      assert.notEqual(await codigo(cliente), anterior);
      assert.match(saida, /Atendimento [a-f0-9]{32} autorizado/);
      assert.ok(!saida.includes(credencial) && !saida.includes(credencialRede));
    } finally {
      await cliente.close();
      await tecnico.close();
    }
  });

  await t.test("usa captura de teste sem seletor, atualiza monitores e troca a geração da entrada", async () => {
    const cliente = await contexto.newPage();
    const tecnico = await contexto.newPage();
    try {
      await Promise.all([cliente.goto(url + "/#captura-nativa"), tecnico.goto(url + "/?modo=tecnico")]);
      await status(cliente, "Aguardando conexão");
      await status(tecnico, "Aguardando código");
      const anterior = await codigo(cliente);
      await solicitar(tecnico, anterior);
      await cliente.getByRole("checkbox", { name: "Permitir mouse e teclado" }).check();
      await cliente.getByRole("button", { name: "Autorizar acesso" }).click();
      await tecnico.waitForFunction(() => document.querySelector("video.controlavel")?.videoWidth === 320);
      assert.equal(await tecnico.getByRole("button", { name: "Tela 1" }).isVisible(), false);
      await cliente.evaluate(() => window.conectarMonitorNoTeste());
      const segunda = tecnico.getByRole("button", { name: "Tela 2" });
      await segunda.click();
      await tecnico.waitForFunction(() => {
        const video = document.querySelector("video.controlavel");
        return video?.videoWidth === 400 && video.videoHeight === 300;
      });
      assert.equal(await segunda.getAttribute("aria-pressed"), "true");
      await tecnico.setViewportSize({ width: 700, height: 500 });
      const video = await tecnico.locator("#tela").boundingBox();
      await tecnico.mouse.click(video.x + video.width / 2, video.y + video.height / 2);
      await cliente.waitForFunction(() => window.geracoesDoTeste.includes(2));
      await tecnico.getByRole("button", { name: "Encerrar", exact: true }).click();
      await status(cliente, "Aguardando conexão");
      assert.notEqual(await codigo(cliente), anterior);
    } finally {
      await Promise.all([cliente.close(), tecnico.close()]);
    }
  });

  await t.test("revoga o acesso ao perder renovação mesmo com WebRTC aberto", async () => {
    const { cliente, tecnico } = await par(contexto, url);
    try {
      await solicitar(tecnico, await codigo(cliente), credencialRede);
      await cliente.getByRole("button", { name: "Autorizar acesso" }).click();
      await status(tecnico, "Conexão WebRTC estabelecida");
      await tecnico.getByText("Cliente de teste", { exact: true }).waitFor();
      await tecnico.evaluate(() => { window.bloquearRenovacaoNoTeste = true; });
      await tecnico.locator("#tela").waitFor({ state: "hidden", timeout: 9000 });
      assert.ok(await tecnico.evaluate(() => window.revogacoesNoTeste) > 0);
      await status(cliente, "Aguardando conexão");
      assert.equal(await tecnico.locator("#tela").isVisible(), false);
      const entradaAnterior = await cliente.evaluate(() => window.entradasDoTeste.length);
      await tecnico.keyboard.press("c");
      assert.equal(await cliente.evaluate(() => window.entradasDoTeste.length), entradaAnterior);
    } finally {
      await cliente.close();
      await tecnico.close();
    }
  });

  await t.test("reconecta após perder a sinalização e recebe um código novo", async () => {
    const cliente = await contexto.newPage();
    try {
      await cliente.goto(url);
      await status(cliente, "Aguardando conexão");
      const anterior = await codigo(cliente);
      await cliente.evaluate(() => window.sinalizacaoNoTeste.close());
      await status(cliente, "Conexão com o servidor encerrada");
      assert.equal(await cliente.locator("#codigo").textContent(), "");
      const novaSinalizacao = cliente.waitForEvent("websocket");
      await cliente.getByRole("button", { name: "Reconectar ao servidor" }).click();
      const socketNovo = await novaSinalizacao;
      assert.match(socketNovo.url(), /^wss:/);
      await status(cliente, "Aguardando conexão");
      assert.notEqual(await codigo(cliente), anterior);
    } finally {
      await cliente.close();
    }
  });

  await t.test("recusa e invalida o código; a credencial é obrigatória", async () => {
    const { cliente, tecnico } = await par(contexto, url);
    try {
      const anterior = await codigo(cliente);
      await solicitar(tecnico, anterior, randomBytes(32).toString("base64url"));
      await status(tecnico, "Credencial do técnico inválida");
      assert.equal(await cliente.getByRole("button", { name: "Autorizar acesso" }).isVisible(), false);
      await solicitar(tecnico, anterior);
      await cliente.getByRole("button", { name: "Recusar acesso" }).click();
      await status(tecnico, "Solicitação recusada pelo cliente");
      await status(cliente, "Aguardando conexão");
      assert.notEqual(await codigo(cliente), anterior);
      await solicitar(tecnico, anterior);
      await status(tecnico, "Código inválido ou indisponível");
    } finally {
      await cliente.close();
      await tecnico.close();
    }
  });

  await t.test("rejeita código expirado e limita tentativas por técnico", async () => {
    const { cliente, tecnico } = await par(contexto, url);
    try {
      const anterior = await codigo(cliente);
      await cliente.waitForFunction((valor) => {
        const exibido = document.querySelector("#codigo").textContent;
        return exibido && exibido !== valor;
      }, anterior, { timeout: (prazoCodigo + 5) * 1000 });
      for (let tentativa = 0; tentativa < 5; tentativa++) {
        await solicitar(tecnico, anterior, credencialLimite);
        await status(tecnico, "Código inválido ou indisponível");
      }
      await solicitar(tecnico, await codigo(cliente), credencialLimite);
      await status(tecnico, "Limite de tentativas atingido");
    } finally {
      await cliente.close();
      await tecnico.close();
    }
  });
});
