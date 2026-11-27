/**
 * WebSocket client for the tmax.net backend.
 *
 * Mirrors Electron's ipcRenderer (invoke / send / on) so the original
 * preload `terminalAPI` can be re-implemented on top of it with the same
 * signatures and the React renderer stays unchanged.
 *
 * Wire format: see src/Tmax.App/Rpc/RpcServer.cs.
 */

type Listener = (...args: any[]) => void;

interface Pending {
  resolve: (value: unknown) => void;
  reject: (reason: Error) => void;
}

export class Backend {
  private socket!: WebSocket;
  private nextId = 1;
  private readonly pending = new Map<number, Pending>();
  private readonly listeners = new Map<string, Set<Listener>>();
  private readonly queue: string[] = [];

  private constructor(private readonly url: string) {}

  /** Reads port/token from the URL fragment the host put there and connects. */
  static async connect(location: Location = window.location): Promise<Backend> {
    const params = new URLSearchParams(location.hash.slice(1));
    const port = params.get('port');
    const token = params.get('token');
    if (!port || !token) throw new Error('Missing backend port/token in URL fragment');
    // Drop the token from the visible URL / history; it stays in memory only.
    history.replaceState(null, '', location.pathname + location.search);

    const backend = new Backend(`ws://127.0.0.1:${port}/ws?token=${encodeURIComponent(token)}`);
    await backend.open();
    return backend;
  }

  invoke<T = unknown>(channel: string, ...args: unknown[]): Promise<T> {
    const id = this.nextId++;
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
      this.transmit({ type: 'invoke', id, channel, args });
    });
  }

  send(channel: string, ...args: unknown[]): void {
    this.transmit({ type: 'send', channel, args });
  }

  /** Subscribe to a backend event. Returns an unsubscribe function (like the preload API). */
  on(channel: string, listener: Listener): () => void {
    let set = this.listeners.get(channel);
    if (!set) this.listeners.set(channel, (set = new Set()));
    set.add(listener);
    return () => set!.delete(listener);
  }

  private open(): Promise<void> {
    return new Promise((resolve, reject) => {
      const socket = new WebSocket(this.url);
      this.socket = socket;
      socket.onopen = () => {
        for (const msg of this.queue.splice(0)) socket.send(msg);
        resolve();
      };
      socket.onerror = () => reject(new Error('Backend connection failed'));
      socket.onclose = () => {
        for (const p of this.pending.values()) p.reject(new Error('Backend connection closed'));
        this.pending.clear();
        this.emit('backend:disconnected');
      };
      socket.onmessage = (ev) => this.receive(ev.data as string);
    });
  }

  private transmit(msg: object): void {
    const json = JSON.stringify(msg);
    if (this.socket?.readyState === WebSocket.OPEN) this.socket.send(json);
    else this.queue.push(json);
  }

  private receive(raw: string): void {
    const msg = JSON.parse(raw);
    if (msg.type === 'reply') {
      const p = this.pending.get(msg.id);
      if (!p) return;
      this.pending.delete(msg.id);
      if (msg.error != null) p.reject(new Error(msg.error));
      else p.resolve(msg.result);
    } else if (msg.type === 'event') {
      this.emit(msg.channel, ...(msg.args ?? []));
    }
  }

  private emit(channel: string, ...args: unknown[]): void {
    for (const l of this.listeners.get(channel) ?? []) {
      try {
        l(...args);
      } catch (err) {
        console.error(`[backend] listener for ${channel} threw`, err);
      }
    }
  }
}
