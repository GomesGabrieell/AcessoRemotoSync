import assert from "node:assert/strict";
import { test } from "node:test";
import { amostrar } from "../src/SyncDesk.Sinalizacao/wwwroot/metricas.js";

test("calcula bitrate por intervalo e separa FPS, RTT e campos indisponíveis", () => {
  assert.equal(amostrar(new Map()).amostra, null);
  const stats = new Map([
    ["transporte", { type: "transport", selectedCandidatePairId: "par" }],
    ["par", { type: "candidate-pair", currentRoundTripTime: 0.025 }],
    ["video", { id: "video", type: "inbound-rtp", kind: "video", timestamp: 2000,
      framesPerSecond: 18, bytesReceived: 200_000 }],
  ]);
  assert.equal(amostrar(stats).kbps, null);
  assert.equal(amostrar(stats, { id: "video", tempo: 1000, bytes: 100_000 }).kbps, 800);
  assert.equal(amostrar(stats).rtt, 25);
  assert.equal(amostrar(stats).fps, 18);
});

test("mede perda por intervalo, renderização, resolução, codec e relay da sessão", () => {
  const stats = new Map([
    ["transporte", { type: "transport", selectedCandidatePairId: "par" }],
    ["par", { type: "candidate-pair", currentRoundTripTime: .1, localCandidateId: "a", remoteCandidateId: "b" }],
    ["a", { candidateType: "srflx" }], ["b", { candidateType: "relay" }],
    ["codec", { mimeType: "video/H264" }],
    ["video", { id: "video", type: "inbound-rtp", kind: "video", timestamp: 2000, codecId: "codec",
      frameWidth: 1920, frameHeight: 1080, packetsReceived: 98, packetsLost: 12, framesDropped: 1, freezeCount: 3 }],
  ]);
  const medidas = amostrar(stats, { id: "video", tempo: 1000, recebidos: 0, perdidos: 10, renderizados: 10 }, 35);
  assert.equal(medidas.perda, .02);
  assert.equal(medidas.render, 25);
  assert.equal(medidas.resolucao, "1920 × 1080");
  assert.equal(medidas.codec, "H264");
  assert.equal(medidas.rota, "Relay (TURN)");
  assert.equal(medidas.descartados, 1);
  assert.equal(medidas.congelamentos, 3);
});
