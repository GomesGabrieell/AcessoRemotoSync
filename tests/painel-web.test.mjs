import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import os from "node:os";
import { fileURLToPath } from "node:url";
import { createServer } from "node:net";
import { test } from "node:test";
import { chromium } from "playwright-core";

const projeto = fileURLToPath(new URL("../src/SyncDesk.Sinalizacao/", import.meta.url));
const scriptAceite = fileURLToPath(new URL("../scripts/testar-aceite-web.ps1", import.meta.url));
const scriptEstimulo = fileURLToPath(new URL("../scripts/estimulo-video.ps1", import.meta.url));
const executavelNativo = process.env.SYNC_DESK_WEB_NATIVO;
const autorizarControleNativo = Boolean(executavelNativo && process.env.SYNC_DESK_WEB_CONTROLE);
const medirVideo = Boolean(executavelNativo && process.env.SYNC_DESK_MEDIR_VIDEO);
const segundosResistencia = executavelNativo ? Math.max(0, Math.min(180, Number(process.env.SYNC_DESK_RESISTENCIA) || 0)) : 0;
const senha = randomBytes(32).toString("base64url");
const outraSenha = randomBytes(32).toString("base64url");

async function portaLivre() {
  const socket = createServer();
  await new Promise((resolve, reject) => socket.once("error", reject).listen(0, "127.0.0.1", resolve));
  const porta = socket.address().port;
  await new Promise((resolve) => socket.close(resolve));
  return porta;
}

test("painel web autoriza um convite único, abre mídia WebRTC e registra o atendimento",
  { timeout: 75_000 + segundosResistencia * 1000 }, async (t) => {
  const dir = await mkdtemp(path.join(os.tmpdir(), "syncdesk-web-"));
  const exe = executavelNativo || path.join(dir, "SyncDesk.exe");
  const historico = path.join(dir, "historico.json");
  if (!executavelNativo) await writeFile(exe, "arquivo de teste");
  const url = executavelNativo ? "http://127.0.0.1:7446" : "https://localhost:" + await portaLivre();
  let agente;
  let fimAgente;
  const processo = spawn(process.env.DOTNET_EXE || "dotnet",
    [path.join(projeto, "bin/Release/net10.0/SyncDesk.Sinalizacao.dll"),
      ...(executavelNativo ? ["--teste-web-local"] : ["--urls", url])],
    { cwd: projeto, env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development", SyncDesk__Tecnicos__Ana: senha,
      SyncDesk__Tecnicos__Marcos: outraSenha, SyncDesk__ArquivoCodigos: path.join(dir, "codigos.bin"),
      SyncDesk__ArquivoHistoricoWeb: historico, SyncDesk__ExecutavelCliente: exe, SyncDesk__ValidadeConviteMinutos: "7" },
      stdio: ["ignore", "pipe", "pipe"] });
  const fim = new Promise((resolve) => processo.once("close", resolve));
  t.after(async () => {
    if (agente) { agente.kill(); await fimAgente; }
    processo.kill();
    await fim;
    await rm(dir, { recursive: true, force: true });
  });
  let saida = "";
  processo.stderr.on("data", (dados) => { saida += dados; });
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(saida)), 15000);
    processo.once("error", (erro) => { clearTimeout(timer); reject(erro); });
    processo.once("exit", () => { clearTimeout(timer); reject(new Error(saida)); });
    processo.stdout.on("data", (dados) => {
      saida += dados;
      if (saida.includes("Now listening on:")) { clearTimeout(timer); resolve(); }
    });
  });
  const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_EXE || undefined,
    args: ["--ignore-certificate-errors", "--autoplay-policy=no-user-gesture-required"] });
  t.after(() => browser.close());
  const navegador = await browser.newContext({ ignoreHTTPSErrors: true, acceptDownloads: true });
  if (medirVideo) await navegador.addInitScript(() => {
    if (location.pathname !== "/sessao") return;
    const Classe = window.RTCPeerConnection;
    window.RTCPeerConnection = class extends Classe {
      constructor(...args) { super(...args); window.parMedido = this; }
    };
  });
  await navegador.grantPermissions(["clipboard-read", "clipboard-write"], { origin: url });
  const outro = await browser.newContext({ ignoreHTTPSErrors: true });
  const painel = await navegador.newPage();
  painel.on("pageerror", (erro) => t.diagnostic(erro.message));
  assert.equal((await navegador.request.get(url + "/api/web/painel")).status(), 401);
  assert.equal((await navegador.request.get(url + "/sessao?id=x")).status(), 401);
  await painel.goto(url + "/painel");
  await painel.getByLabel("Usuário").fill("Ana");
  await painel.getByLabel("Credencial", { exact: true }).fill(senha);
  await painel.getByRole("button", { name: "Entrar", exact: true }).click();
  await painel.getByRole("heading", { name: "Link de atendimento" }).waitFor();
  assert.equal(await painel.getByRole("navigation").getByRole("link").count(), 1);
  await painel.getByRole("link", { name: "Visão geral", exact: true }).waitFor();
  assert.deepEqual(await painel.locator("main h1, main h2").allTextContents(),
    ["Link de atendimento", "Atendimentos ativos", "Últimos atendimentos"]);
  assert.equal(await painel.getByRole("textbox", { name: "Cliente", exact: true }).count(), 0);
  await painel.setViewportSize({ width: 390, height: 844 });
  assert.ok(await painel.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
  await painel.setViewportSize({ width: 1280, height: 720 });
  const [cookie] = await navegador.cookies(url);
  assert.equal(cookie.secure, !executavelNativo);
  assert.ok(cookie.httpOnly && cookie.sameSite === "Strict");
  assert.equal((await navegador.request.post(url + "/api/web/convites", { data: {} })).status(), 403);
  assert.equal((await outro.request.post(url + "/api/web/entrar",
    { headers: { Origin: url }, data: { nome: "Marcos", credencial: outraSenha } })).status(), 204);

  await painel.getByRole("button", { name: "Novo link", exact: true }).click();
  const link = painel.getByRole("textbox", { name: "Link de atendimento" });
  await painel.getByRole("button", { name: "Copiar link", exact: true }).click();
  await painel.getByText("Link copiado.", { exact: true }).waitFor();
  const urlConvite = await link.inputValue();
  assert.equal(await painel.evaluate(() => navigator.clipboard.readText()), urlConvite);
  const acessar = painel.locator("#ativos").getByRole("button", { name: "Acessar", exact: true });
  await acessar.click();
  const statusConvite = painel.locator("#status-convite");
  await statusConvite.filter({ hasText: "Envie o link ao cliente para abrir no navegador." }).waitFor();
  assert.equal(await statusConvite.evaluate((elemento) => document.activeElement === elemento), true);
  assert.equal(await painel.locator("#visualizador").isVisible(), false);
  assert.equal(await link.inputValue(), urlConvite);
  if (process.env.SYNC_DESK_CAPTURAS)
    await painel.screenshot({ path: path.join(process.env.SYNC_DESK_CAPTURAS, "painel-web.png"),
      mask: [link], maskColor: "#141a1b" });
  const { registro, vence } = (await (await navegador.request.get(url + "/api/web/painel")).json()).atuais[0];
  const { id } = registro;
  assert.equal(Date.parse(vence) - Date.parse(registro.criado), 7 * 60_000);
  assert.equal((await outro.request.get(url + "/sessao?id=" + id)).status(), 404);
  assert.equal((await outro.request.delete(url + "/api/web/convites/" + id, { headers: { Origin: url } })).status(), 404);
  const clienteCtx = await browser.newContext({ ignoreHTTPSErrors: true, acceptDownloads: true });
  const convite = await clienteCtx.newPage();
  await convite.goto(urlConvite);
  const download = convite.waitForEvent("download");
  await convite.getByRole("link", { name: "Baixar SyncDesk para Windows" }).click();
  const baixado = await download;
  assert.match(baixado.suggestedFilename(), executavelNativo ? /^SyncDesk-web-local-[a-f\d]{48}\.exe$/ :
    /^SyncDesk-[a-f\d]{48}\.exe$/);
  const chave = urlConvite.split("/").at(-1);
  await clienteCtx.addInitScript((token) => {
    window.syncDeskServidor = (location.protocol === "https:" ? "wss://" : "ws://") + location.host;
    window.syncDeskCapturaNativa = true;
    window.syncDeskConvite = token;
    const ponte = new EventTarget();
    const responder = (data) => ponte.dispatchEvent(new MessageEvent("message", { data }));
    const canvas = document.createElement("canvas");
    canvas.width = 320;
    canvas.height = 200;
    const ctx = canvas.getContext("2d");
    ctx.fillStyle = "green";
    ctx.fillRect(0, 0, 320, 200);
    const jpeg = canvas.toDataURL("image/jpeg").split(",")[1];
    ponte.postMessage = ({ tipo, controle }) => {
      if (tipo === "atendimentoNativo") {
        responder({ tipo: "permissao", controle, computador: "PC de teste" });
        responder({ tipo: "monitores", selecionado: "1", geracao: 1, telas: [{ id: "1", numero: 1, autorizado: true }] });
      }
      if (tipo === "capturar") responder({ tipo: "quadro", geracao: 1, jpeg });
    };
    window.chrome ??= {};
    window.chrome.webview = ponte;
  }, chave);
  const cliente = executavelNativo ? null : await clienteCtx.newPage();
  if (executavelNativo) {
    const caminho = path.join(dir, baixado.suggestedFilename());
    await baixado.saveAs(caminho);
    agente = spawn(caminho, [], { stdio: "ignore" });
    fimAgente = new Promise((resolve) => agente.once("close", resolve));
    await new Promise((resolve, reject) => { agente.once("spawn", resolve); agente.once("error", reject); });
    const vincular = painel.locator("#status-convite").filter({ hasText: "Aguardando autorização do cliente" });
    await vincular.waitFor();
  } else {
    await cliente.goto(url);
    await cliente.getByText("Técnico: Ana").waitFor();
  }
  assert.equal(await painel.locator("#visualizador").isVisible(), false);
  assert.equal((await convite.request.get(url + "/api/convite/" + chave)).status(), 404);
  const duplicado = await clienteCtx.newPage();
  await duplicado.goto(url);
  await duplicado.getByText(/Convite inválido/).waitFor();
  await duplicado.close();
  if (executavelNativo) {
    await new Promise((resolve, reject) => {
      const aceite = spawn("powershell.exe", ["-NoProfile", "-STA", "-File", scriptAceite, "-Id", String(agente.pid),
        ...(autorizarControleNativo ? ["-Controle"] : [])]);
      let saidaAceite = "";
      aceite.stderr.on("data", (dados) => { saidaAceite += dados; });
      aceite.once("error", reject);
      aceite.once("close", (codigo) => codigo === 0 ? resolve() : reject(new Error(saidaAceite)));
    });
  } else {
    const permissao = cliente.getByRole("checkbox", { name: "Permitir mouse e teclado" });
    assert.equal(await permissao.isChecked(), false);
    await permissao.check();
    await cliente.getByRole("button", { name: "Autorizar acesso" }).click();
  }
  const visor = painel.frameLocator("#visualizador");
  await visor.locator("#tela").waitFor();
  await visor.locator('[aria-label="Tela compartilhada pelo cliente · Tela 1"]').waitFor();
  if (!executavelNativo || autorizarControleNativo) await visor.locator("video.controlavel").waitFor();
  const computador = await visor.locator("#computador-remoto").textContent();
  assert.equal(computador.toLowerCase(), (executavelNativo ? os.hostname() : "PC de teste").toLowerCase());
  assert.equal((await visor.locator(".barra-sessao").boundingBox()).height, 32);
  if (process.env.SYNC_DESK_ESPERAR_PIXELS) {
    await visor.getByText("Ações", { exact: true }).click();
    await visor.getByRole("button", { name: "Estatísticas da conexão" }).click();
    const fontePixels = visor.locator("#metrica-fonte").filter({ hasText: /^(DXGI|GDI)$/ });
    await fontePixels.waitFor({ timeout: 10000 });
    t.diagnostic("Captura nativa por " + await fontePixels.textContent());
    if (!medirVideo) await visor.locator("#metricas > summary").click();
  }
  if (medirVideo) {
    const cena = spawn("powershell.exe", ["-NoProfile", "-STA", "-File", scriptEstimulo, "-Segundos", "15"]);
    const fechada = new Promise((resolve) => cena.once("close", resolve));
    try {
      await new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(Error("A cena de medição não abriu.")), 10000);
        cena.stdout.on("data", (linha) => {
          if (String(linha).includes("CENA_PRONTA")) { clearTimeout(timer); resolve(); }
        });
        cena.on("error", (erro) => { clearTimeout(timer); reject(erro); });
        cena.on("exit", (codigo) => { clearTimeout(timer); reject(Error("A cena fechou com código " + codigo)); });
      });
      const dadosVideo = await visor.locator("#tela").evaluate(async () => {
        const medir = async () => {
          const stats = await window.parMedido.getStats();
          const itens = [...stats.values()];
          const video = itens.find((item) => item.kind === "video" && item.type === "inbound-rtp");
          if (!video) throw Error("Fluxo de vídeo indisponível.");
          const transporte = itens.find((item) => item.type === "transport" && item.selectedCandidatePairId);
          const par = stats.get(transporte?.selectedCandidatePairId);
          return { tempo: video.timestamp, quadros: video.framesDecoded, descartados: video.framesDropped,
            bytes: video.bytesReceived, congelamentos: video.freezeCount, largura: video.frameWidth,
            altura: video.frameHeight, codec: stats.get(video.codecId)?.mimeType, rtt: par?.currentRoundTripTime };
        };
        await new Promise((resolve) => setTimeout(resolve, 2500));
        const a = await medir();
        await new Promise((resolve) => setTimeout(resolve, 6500));
        const b = await medir();
        const segundos = (b.tempo - a.tempo) / 1000;
        return { fps: Number(((b.quadros - a.quadros) / segundos).toFixed(1)), resolucao: b.largura + "×" + b.altura,
          kbps: Math.round((b.bytes - a.bytes) * 8 / 1000 / segundos), codec: b.codec,
          descartados: b.descartados - a.descartados, congelamentos: b.congelamentos - a.congelamentos,
          rttMs: Number((b.rtt * 1000).toFixed(1)) };
      });
      t.diagnostic("Medição WebRTC " + JSON.stringify(dadosVideo));
      if (process.env.SYNC_DESK_ESPERAR_PIXELS) {
        const painelVideo = await visor.locator("#metricas").evaluate((elemento) =>
          Object.fromEntries(["captura", "envio", "render", "capturaMs", "codificacao", "cpu", "modo"]
            .map((id) => [id, elemento.querySelector("#metrica-" + id).textContent])));
        t.diagnostic("Telemetria do vídeo " + JSON.stringify(painelVideo));
        await visor.locator("#metricas > summary").click();
      }
    } finally {
      cena.kill();
      await fechada;
    }
  }
  if (executavelNativo && await visor.getByRole("button", { name: "Tela 2", exact: true }).isVisible()) {
    await visor.getByRole("button", { name: "Tela 2", exact: true }).click();
    await visor.locator('[aria-label="Tela compartilhada pelo cliente · Tela 2"]').waitFor();
    t.diagnostic("Vídeo nativo validado também no segundo monitor.");
  }
  if (segundosResistencia > 0) {
    const video = visor.locator("#tela");
    const inicio = Date.now();
    while (Date.now() - inicio < segundosResistencia * 1000) {
      const anterior = await video.evaluate((elemento) => elemento.currentTime);
      await new Promise((resolve) => setTimeout(resolve, 8000));
      const atual = await video.evaluate((elemento) => elemento.currentTime);
      assert.ok(atual > anterior, "A imagem deixou de avançar durante o teste de estabilidade.");
      assert.equal(await video.isVisible(), true);
    }
    t.diagnostic("Vídeo contínuo durante " + Math.round((Date.now() - inicio) / 1000) + " segundos.");
  }
  if (!medirVideo || process.env.SYNC_DESK_ESPERAR_PIXELS) {
    await visor.locator("#indicador-qualidade > summary").click();
    await visor.getByRole("button", { name: "Economia de dados", exact: true }).click();
    await visor.locator("#indicador-qualidade > summary").filter({ hasText: "Economia de dados" }).waitFor();
    assert.equal(await visor.locator("#tela").isVisible(), true);
  }
  await visor.getByRole("button", { name: "Encerrar", exact: true }).click();
  await painel.locator("#retorno").filter({ hasText: "O técnico encerrou o atendimento." }).waitFor();
  await painel.locator("#ultimos").getByRole("cell", { name: computador, exact: true }).waitFor();
  const gravado = JSON.parse(await readFile(historico, "utf8"));
  assert.equal(gravado[0].Status, "encerrado");
  assert.equal(gravado[0].Dono, "Ana");
  assert.ok(gravado[0].Inicio && gravado[0].Fim && !JSON.stringify(gravado).includes(chave));
  assert.equal((await (await outro.request.get(url + "/api/web/painel")).json()).historico.length, 0);
  await painel.getByRole("button", { name: "Novo link", exact: true }).click();
  await painel.getByRole("button", { name: "Copiar link", exact: true }).click();
  const cancelado = await link.inputValue();
  await painel.reload();
  await painel.locator("#ativos").getByRole("button", { name: "Acessar", exact: true }).click();
  await painel.locator("#status-convite").filter({ hasText: "Aguardando o cliente abrir o link." }).waitFor();
  assert.equal(await link.inputValue(), cancelado);
  const abertos = (await (await navegador.request.get(url + "/api/web/painel")).json()).atuais;
  const canceladoId = abertos.find((c) => c.url === new URL(cancelado).pathname).registro.id;
  assert.equal((await navegador.request.delete(url + "/api/web/convites/" + canceladoId, { headers: { Origin: url } })).status(), 204);
  assert.equal((await convite.request.get(cancelado)).status(), 404);

  await painel.getByRole("button", { name: "Novo link", exact: true }).click();
  await painel.getByRole("button", { name: "Copiar link", exact: true }).click();
  const linkNavegador = await link.inputValue();
  const contextoWeb = await browser.newContext({ ignoreHTTPSErrors: true });
  await contextoWeb.addInitScript(() => {
    window.pedidosDeCaptura = 0;
    navigator.mediaDevices.getDisplayMedia = async () => {
      window.pedidosDeCaptura++;
      const canvas = document.createElement("canvas");
      canvas.width = 320;
      canvas.height = 200;
      const contexto = canvas.getContext("2d");
      const desenhar = () => {
        contexto.fillStyle = contexto.fillStyle === "#008000" ? "blue" : "green";
        contexto.fillRect(0, 0, 320, 200);
        requestAnimationFrame(desenhar);
      };
      desenhar();
      return canvas.captureStream(20);
    };
  });
  const clienteWeb = await contextoWeb.newPage();
  await clienteWeb.goto(linkNavegador);
  const continuar = clienteWeb.getByRole("link", { name: "Continuar no navegador" });
  await continuar.waitFor();
  await clienteWeb.getByRole("link", { name: "Baixar SyncDesk para Windows" }).waitFor();
  await acessar.click();
  await continuar.click();
  await clienteWeb.getByText("Técnico: Ana", { exact: true }).waitFor();
  assert.equal(new URL(clienteWeb.url()).hash, "");
  assert.equal(await clienteWeb.evaluate(() => window.pedidosDeCaptura), 0);
  assert.equal(await clienteWeb.locator("#linha-controle").isVisible(), false);
  assert.equal(await painel.locator("#visualizador").isVisible(), false);
  await clienteWeb.getByRole("button", { name: "Escolher tela para compartilhar" }).click();
  await clienteWeb.getByRole("heading", { name: "Atendimento ativo" }).waitFor();
  await visor.locator("#tela").waitFor();
  await visor.getByText("Cliente no navegador", { exact: true }).waitFor();
  assert.equal(await visor.locator("#mostrar-arquivos").isEnabled(), false);
  await clienteWeb.locator("#chat-cliente").click();
  await clienteWeb.getByRole("textbox", { name: "Mensagem" }).fill("Olá, técnico");
  await clienteWeb.getByRole("textbox", { name: "Mensagem" }).press("Enter");
  await visor.getByText("Cliente · Olá, técnico", { exact: true }).waitFor();
  await clienteWeb.getByRole("button", { name: "Encerrar atendimento" }).click();
  await clienteWeb.getByText(/Solicite um novo link/).waitFor();
  await painel.locator("#ultimos").getByRole("cell", { name: "Cliente no navegador", exact: true }).waitFor();
  assert.equal(await painel.locator("#visualizador").isVisible(), false);
  assert.equal((await contextoWeb.request.get(linkNavegador)).status(), 404);

  const categoriasComSegredo = saida.split(/\r?\n/).filter((linha) => linha.includes(chave) || linha.includes(senha))
    .map((linha) => { try { return JSON.parse(linha).Category; } catch { return "saída sem categoria"; } });
  assert.deepEqual(categoriasComSegredo, []);
});
