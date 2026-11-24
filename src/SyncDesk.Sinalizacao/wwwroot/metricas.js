export function amostrar(stats, anterior, apresentacao) {
  let midia, transporte;
  for (const item of stats.values()) {
    if (item.type === "inbound-rtp" && item.kind === "video") midia = item;
    if (item.type === "transport" && item.selectedCandidatePairId) transporte = item;
  }
  const par = transporte && stats.get(transporte.selectedCandidatePairId);
  const amostra = midia ? { id: midia.id, bytes: midia.bytesReceived, tempo: midia.timestamp,
    recebidos: midia.packetsReceived, perdidos: midia.packetsLost, renderizados: apresentacao } : null;
  const delta = amostra && anterior?.id === amostra.id ? amostra.tempo - anterior.tempo : 0;
  const recebidos = delta > 0 ? amostra.recebidos - anterior.recebidos : NaN;
  const perdidos = delta > 0 ? Math.max(0, amostra.perdidos - anterior.perdidos) : NaN;
  const renderizados = delta > 0 ? amostra.renderizados - anterior.renderizados : NaN;
  const local = par && stats.get(par.localCandidateId);
  const remoto = par && stats.get(par.remoteCandidateId);
  const codec = midia && stats.get(midia.codecId);
  return {
    amostra,
    rtt: Number.isFinite(par?.currentRoundTripTime) ? par.currentRoundTripTime * 1000 : null,
    fps: Number.isFinite(midia?.framesPerSecond) ? midia.framesPerSecond : null,
    kbps: delta > 0 && amostra.bytes >= anterior.bytes ? (amostra.bytes - anterior.bytes) * 8 / delta : null,
    perda: recebidos >= 0 && recebidos + perdidos > 0 ? perdidos / (recebidos + perdidos) : null,
    render: renderizados >= 0 ? renderizados * 1000 / delta : null,
    resolucao: midia?.frameWidth && midia?.frameHeight ? midia.frameWidth + " × " + midia.frameHeight : null,
    descartados: midia?.framesDropped,
    congelamentos: midia?.freezeCount,
    codec: codec?.mimeType?.replace(/^video\//, ""),
    rota: local && remoto ? [local, remoto].some((c) => c.candidateType === "relay") ? "Relay (TURN)" : "Direta" : null,
  };
}
