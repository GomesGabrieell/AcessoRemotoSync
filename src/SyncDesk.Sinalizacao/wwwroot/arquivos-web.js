const limiteArquivo = 32 * 1024 * 1024;
const limiteLote = 64 * 1024 * 1024;
const bloco = 11 * 1024;
const janela = 12;
const idValido = (id) => typeof id === "string" && /^[0-9a-f]{32}$/.test(id);
const gerarId = () => crypto.randomUUID().replaceAll("-", "");
const nomeValido = (nome) => typeof nome === "string" && nome.length > 0 && nome.length <= 255 &&
  !/[/\\:\x00-\x1f]/.test(nome) && nome !== "." && nome !== "..";
const paraBase64 = (bytes) => btoa(String.fromCharCode(...bytes));
const deBase64 = (texto) => Uint8Array.from(atob(texto), (c) => c.charCodeAt(0));
const hash = async (conteudo) => paraBase64(new Uint8Array(await crypto.subtle.digest("SHA-256", conteudo)));

export class ArquivosWeb {
  #transmitir;
  #ativo;
  #progresso;
  #oferta;
  #baixar;
  #enviando;
  #recebendo;
  #locais = [];
  #remota = "";
  #local = "";
  #versao = 0;
  #fila = Promise.resolve();
  #pendentes = 0;
  #fechado = false;

  constructor(transmitir, ativo, progresso, oferta, baixar) {
    this.#transmitir = transmitir;
    this.#ativo = ativo;
    this.#progresso = progresso;
    this.#oferta = oferta;
    this.#baixar = baixar;
  }

  #enviar(tipo, id, valor = 0, texto = "", dados) {
    if (!this.#fechado && this.#ativo()) this.#transmitir({ tipo, id, valor, texto, dados });
  }

  selecionar(arquivos) {
    if (this.#enviando || this.#fechado) return;
    const lista = [...arquivos];
    if (!lista.length) return;
    const total = lista.reduce((soma, a) => soma + a.size, 0);
    if (lista.length > 64 || total > limiteLote || lista.some((a) => a.size > limiteArquivo || !nomeValido(a.name))) {
      this.#status(true, false, "Selecione até 64 arquivos, com 32 MiB por arquivo e 64 MiB no total.");
      return;
    }
    this.#locais = lista;
    this.#local = gerarId();
    this.#remota = "";
    this.#oferta(false);
    this.#versao = (Math.floor(this.#versao / 2) + 1) * 2 + 1;
    this.#enviar("oferta", this.#local, this.#versao);
    this.#status(true, false, "Arquivos disponíveis. Cole com Ctrl+V na pasta do Explorador remoto.");
  }

  solicitar() {
    if (!this.#remota || this.#recebendo || this.#fechado) return;
    const r = this.#recebendo = { id: gerarId(), inicio: performance.now(), bytes: 0, total: 0,
      quantidade: 0, arquivos: [] };
    this.#oferta(false);
    this.#vigiar(r);
    this.#enviar("colar", r.id, 0, this.#remota);
    this.#status(false, true, "Solicitando arquivos…", r);
  }

  receber(mensagem) {
    if (this.#fechado) return;
    if (this.#pendentes >= 64) { this.fechar(); return; }
    this.#pendentes++;
    this.#fila = this.#fila.then(async () => {
      if (!this.#fechado && this.#ativo()) await this.#tratar(mensagem);
    }).catch(() => this.#cancelarRecepcao("Arquivo inválido ou acima do limite do navegador."))
      .finally(() => { this.#pendentes--; });
  }

  async #tratar(m) {
    if (m.tipo === "oferta") {
      if (!Number.isSafeInteger(m.valor) || m.valor <= this.#versao || m.valor > 2_000_000_000 ||
          m.valor % 2 || (m.id !== "" && !idValido(m.id))) return;
      this.#versao = m.valor;
      this.#remota = m.id;
      this.#local = "";
      this.#locais = [];
      this.#oferta(Boolean(this.#remota && !this.#recebendo));
      return;
    }
    if (!idValido(m.id)) return;
    if (m.tipo === "colar") {
      if (this.#enviando || !this.#local || m.texto !== this.#local) this.#enviar("erro", m.id);
      else void this.#enviarLote(m.id, this.#locais);
      return;
    }
    const envio = this.#enviando;
    if (envio?.id === m.id) {
      if (m.tipo === "erro" || m.tipo === "cancelar") this.#cancelarEnvio();
      else if (envio.espera?.tipo === m.tipo && envio.espera?.valor === m.valor) envio.espera.resolver();
      return;
    }

    const r = this.#recebendo;
    if (!r || r.id !== m.id) return;
    this.#vigiar(r);
    switch (m.tipo) {
      case "lote":
        if (r.quantidade || !Number.isInteger(m.valor) || m.valor < 1 || m.valor > 64 ||
            typeof m.texto !== "string" || !/^\d+$/.test(m.texto) || Number(m.texto) > limiteLote) throw Error();
        r.quantidade = m.valor;
        r.total = Number(m.texto);
        break;
      case "arquivo":
        if (r.arquivo || r.arquivos.length >= r.quantidade || !nomeValido(m.texto) ||
            !Number.isInteger(m.valor) || m.valor < 0 || m.valor > limiteArquivo || m.valor > r.total - r.bytes) throw Error();
        r.arquivo = { nome: m.texto, dados: new Uint8Array(m.valor), posicao: 0 };
        this.#enviar("pronto", r.id, r.arquivos.length);
        this.#status(false, true, m.texto, r);
        break;
      case "bloco": {
        const a = r.arquivo;
        if (!a || m.valor !== a.posicao || typeof m.dados !== "string") throw Error();
        const bytes = deBase64(m.dados);
        if (!bytes.length || bytes.length > bloco || bytes.length > a.dados.length - a.posicao) throw Error();
        a.dados.set(bytes, a.posicao);
        a.posicao += bytes.length;
        r.bytes += bytes.length;
        break;
      }
      case "janela":
        if (!r.arquivo || m.valor !== r.arquivo.posicao) throw Error();
        this.#enviar("janela", r.id, m.valor);
        this.#status(false, true, r.arquivo.nome, r);
        break;
      case "fimArquivo": {
        const a = r.arquivo;
        if (!a || a.posicao !== a.dados.length || m.dados !== await hash(a.dados)) throw Error();
        if (this.#recebendo !== r || this.#fechado || !this.#ativo()) return;
        this.#enviar("gravado", r.id, r.arquivos.length, a.nome);
        r.arquivos.push({ nome: a.nome, blob: new Blob([a.dados], { type: "application/octet-stream" }) });
        r.arquivo = null;
        break;
      }
      case "fim":
        if (!r.quantidade || r.arquivo || r.bytes !== r.total || r.arquivos.length !== r.quantidade) throw Error();
        this.#enviar("concluido", r.id, r.arquivos.length);
        this.#liberarRecepcao();
        this.#baixar(r.arquivos);
        this.#status(false, false, "Arquivos conferidos. Selecione o nome para baixar.");
        break;
      case "cancelar":
      case "erro":
        this.#liberarRecepcao();
        this.#status(false, false, "O cliente interrompeu o envio.");
        break;
      default: throw Error();
    }
  }

  #vigiar(r) {
    clearTimeout(r.timer);
    r.timer = setTimeout(() => this.#cancelarRecepcao("O cliente deixou de responder."), 35_000);
  }

  #liberarRecepcao() {
    clearTimeout(this.#recebendo?.timer);
    this.#recebendo = undefined;
    this.#oferta(Boolean(this.#remota));
  }

  #cancelarRecepcao(texto) {
    const r = this.#recebendo;
    if (!r) return;
    this.#liberarRecepcao();
    this.#enviar("cancelar", r.id);
    this.#status(false, false, texto);
  }

  #cancelarEnvio(notificar = false) {
    const envio = this.#enviando;
    if (!envio) return;
    this.#enviando = undefined;
    envio.espera?.rejeitar();
    if (notificar) this.#enviar("cancelar", envio.id);
    this.#status(true, false, "Envio cancelado.");
  }

  cancelar(envio) {
    if (envio) this.#cancelarEnvio(true);
    else this.#cancelarRecepcao("Recebimento cancelado.");
  }

  #esperar(e, tipo, valor, iniciar) {
    return new Promise((resolve, reject) => {
      const acabar = (fn) => {
        clearTimeout(timer);
        e.espera = undefined;
        fn();
      };
      const timer = setTimeout(() => acabar(reject), 35_000);
      e.espera = { tipo, valor, resolver: () => acabar(resolve), rejeitar: () => acabar(reject) };
      iniciar();
    });
  }

  async #enviarLote(id, arquivos) {
    const e = this.#enviando = { id, bytes: 0, total: arquivos.reduce((soma, a) => soma + a.size, 0),
      inicio: performance.now() };
    const verificar = () => {
      if (this.#enviando !== e || this.#fechado || !this.#ativo()) throw Error();
    };
    try {
      this.#status(true, true, "Enviando arquivos…", e);
      this.#enviar("lote", id, arquivos.length, String(e.total));
      for (const [i, arquivo] of arquivos.entries()) {
        const bytes = new Uint8Array(await arquivo.arrayBuffer());
        verificar();
        await this.#esperar(e, "pronto", i, () => this.#enviar("arquivo", id, bytes.length, arquivo.name));
        let posicao = 0;
        while (posicao < bytes.length) {
          verificar();
          for (let k = 0; k < janela && posicao < bytes.length; k++) {
            const trecho = bytes.subarray(posicao, posicao + bloco);
            this.#enviar("bloco", id, posicao, "", paraBase64(trecho));
            e.bytes += trecho.length;
            posicao += trecho.length;
          }
          await this.#esperar(e, "janela", posicao, () => this.#enviar("janela", id, posicao));
          this.#status(true, true, arquivo.name, e);
        }
        const integridade = await hash(bytes);
        verificar();
        await this.#esperar(e, "gravado", i, () => this.#enviar("fimArquivo", id, 0, "", integridade));
      }
      verificar();
      await this.#esperar(e, "concluido", arquivos.length, () => this.#enviar("fim", id));
      this.#status(true, false, "Arquivos enviados e conferidos no computador do cliente.");
    } catch {
      if (this.#enviando === e) {
        this.#enviar("erro", id);
        this.#status(true, false, "Envio interrompido. Tente novamente.");
      }
    } finally {
      if (this.#enviando === e) this.#enviando = undefined;
    }
  }

  #status(envio, ativo, texto, dados) {
    this.#progresso({ envio, ativo, texto, bytes: dados?.bytes ?? 0, total: dados?.total ?? 0,
      velocidade: dados ? dados.bytes / Math.max(.1, (performance.now() - dados.inicio) / 1000) : 0 });
  }

  fechar() {
    this.#fechado = true;
    this.#cancelarEnvio();
    this.#remota = this.#local = "";
    this.#liberarRecepcao();
    this.#locais = [];
  }
}
