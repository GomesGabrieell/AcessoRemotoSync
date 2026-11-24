import assert from "node:assert/strict";
import { test } from "node:test";
import { pontoNoVideo } from "../src/SyncDesk.Sinalizacao/wwwroot/controle.js";
import { ajustarNivel, fatorDeResolucao, nivelParaBanda, perfilPara } from "../src/SyncDesk.Sinalizacao/wwwroot/qualidade.js";

test("mapeia os mesmos pontos após redimensionar o vídeo ou alterar o DPI", () => {
  assert.deepEqual(pontoNoVideo({ left: 100, top: 50, width: 400, height: 400 },
    1920, 1080, 200, 250), { x: 0.25, y: 0.5 });
  assert.deepEqual(pontoNoVideo({ left: 0, top: 0, width: 800, height: 450 },
    2560, 1440, 200, 225), { x: 0.25, y: 0.5 });
  assert.deepEqual(pontoNoVideo({ left: 4, top: 10, width: 600, height: 900 },
    1080, 1920, 304, 460), { x: 0.5, y: 0.5 });
});

test("ignora as faixas do vídeo e mantém a soltura de arrasto nos limites", () => {
  const rect = { left: 0, top: 0, width: 400, height: 400 };
  assert.equal(pontoNoVideo(rect, 1920, 1080, 200, 15), null);
  assert.deepEqual(pontoNoVideo(rect, 1920, 1080, 200, 15, true), { x: 0.5, y: 0 });
  assert.equal(pontoNoVideo(rect, 0, 0, 200, 15), null);
});

test("reduz o perfil conforme a capacidade de envio", () => {
  assert.equal(nivelParaBanda(undefined), undefined);
  assert.deepEqual([500_000, 1_000_000, 2_000_000, 5_000_000].map(nivelParaBanda), [0, 1, 2, 3]);
});

test("limita o maior lado do vídeo em paisagem ou retrato", () => {
  assert.equal(fatorDeResolucao(3840, 2160, 1920), 2);
  assert.equal(fatorDeResolucao(2160, 3840, 1920), 2);
  assert.equal(fatorDeResolucao(900, 1600, 1920), 1);
});

test("reduz a qualidade sob perda ou CPU e sobe após três amostras estáveis", () => {
  assert.deepEqual(ajustarNivel(3, 1, { banda: 1_300_000, perda: .08 }), { nivel: 1, boa: 0 });
  assert.deepEqual(ajustarNivel(3, 1, { banda: 7_000_000, perda: .02, rtt: 340 }), { nivel: 2, boa: 0 });
  assert.deepEqual(ajustarNivel(2, 0, { banda: 600_000, movimento: .30 }), { nivel: 0, boa: 0 });
  assert.deepEqual(ajustarNivel(2, 1, { cpu: true }), { nivel: 1, boa: 0 });
  assert.deepEqual(ajustarNivel(2, 0, { banda: 5_000_000, perda: 0 }), { nivel: 2, boa: 1 });
  assert.deepEqual(ajustarNivel(2, 2, { banda: 5_000_000, perda: 0 }), { nivel: 3, boa: 0 });
  assert.deepEqual(ajustarNivel(3, 0, { banda: 1_300_000, movimento: .02 }), { nivel: 3, boa: 0 });
});

test("perfis priorizam leitura, movimento ou economia sem remover limites de rede", () => {
  assert.equal(perfilPara("maxima", 4, .35).largura, 3840);
  assert.equal(perfilPara("desempenho", 4, .35).fps, 60);
  assert.equal(perfilPara("desempenho", 0, .35).fps, 12);
  assert.equal(perfilPara("economia", 4, .35).largura, 1280);
  assert.equal(perfilPara("automatica", 3, .03).largura, 2560);
  assert.equal(perfilPara("automatica", 3, .30).largura, 1920);
});
