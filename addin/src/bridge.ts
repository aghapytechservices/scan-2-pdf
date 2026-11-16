// Client for the local Scan 2 PDF Bridge (http://127.0.0.1:52801).

export type ScanSource = "flatbed" | "feeder" | "duplex";
export type ColorMode = "color" | "gray" | "blackWhite";
export type PageSize = "letter" | "legal" | "a4";

export interface ScannerInfo {
  id: string;
  name: string;
  manufacturer: string;
}

export interface ScanRequest {
  deviceId: string | null;
  source: ScanSource;
  dpi: number;
  color: ColorMode;
  pageSize: PageSize;
  jpegQuality: number;
}

/** Thrown when the bridge can't be reached at all (not running, or blocked by Local Network Access). */
export class BridgeUnreachableError extends Error {}

export class BridgeError extends Error {
  constructor(message: string, public status: number) {
    super(message);
  }
}

// `targetAddressSpace` tells Chromium (Chrome/Edge/WebView2) up-front that this is a loopback request,
// so Local Network Access can show its permission prompt instead of failing the request outright.
type LnaRequestInit = RequestInit & { targetAddressSpace?: "loopback" | "local" };

export class ScanBridge {
  constructor(private baseUrl: string) {}

  setBaseUrl(url: string) {
    this.baseUrl = url.replace(/\/+$/, "");
  }

  private async request<T>(path: string, init: LnaRequestInit = {}, timeoutMs?: number): Promise<T> {
    const controller = new AbortController();
    const timer = timeoutMs ? setTimeout(() => controller.abort(), timeoutMs) : undefined;
    let res: Response;
    try {
      res = await fetch(`${this.baseUrl}${path}`, {
        ...init,
        targetAddressSpace: "loopback",
        signal: controller.signal,
      } as LnaRequestInit);
    } catch {
      throw new BridgeUnreachableError("Scan bridge unreachable");
    } finally {
      if (timer) clearTimeout(timer);
    }

    if (!res.ok) {
      let detail = `${res.status} ${res.statusText}`;
      try {
        const body = await res.json();
        detail = body.detail ?? body.title ?? detail;
      } catch {
        /* non-JSON error */
      }
      throw new BridgeError(detail, res.status);
    }
    return (await res.json()) as T;
  }

  health() {
    return this.request<{ ok: boolean; version: string; edition?: "store" | "standalone" }>("/health", {}, 3000);
  }

  /** Store edition only: asks the bridge to show the Microsoft Store rating dialog on this PC. */
  requestStoreReview() {
    return this.request<{ status: string }>("/store-review", { method: "POST" }, 120000);
  }

  listScanners() {
    return this.request<ScannerInfo[]>("/scanners", {}, 15000);
  }

  /** Returns one base64 JPEG per scanned page. No timeout: a full feeder can take a while. */
  async scan(req: ScanRequest): Promise<string[]> {
    const res = await this.request<{ pages: string[] }>("/scan", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(req),
    });
    return res.pages;
  }
}
