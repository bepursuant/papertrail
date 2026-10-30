export declare const DEFAULT_PORT: 47115;

export type ColorMode = 'color' | 'gray' | 'blackWhite';
export type PaperSource = 'auto' | 'flatbed' | 'feeder';
export type ScanStatus = 'queued' | 'scanning' | 'completed' | 'failed' | 'canceled';
/** Only 'png' (one image per page) today. */
export type OutputFormat = 'png';

export interface ScanOptions {
  scannerId?: string;
  dpi?: number;
  colorMode?: ColorMode;
  source?: PaperSource;
  duplex?: boolean;
  showDriverUi?: boolean;
  maxPages?: number;
  format?: OutputFormat;
  /** Find barcodes on every page. */
  barcodes?: boolean;
  /** Formats to look for (needs `barcodes: true`); default: every format except aztec, code93 and codabar. */
  barcodeFormats?: BarcodeFormat[];
}

export type BarcodeFormat =
  | 'qrCode' | 'dataMatrix' | 'pdf417' | 'aztec'
  | 'code128' | 'code39' | 'code93' | 'codabar'
  | 'ean13' | 'ean8' | 'upcA' | 'upcE' | 'itf';

/** 'pending' until analysed; 'partial' when the time budget ran out; 'skipped' when not analysed. */
export type BarcodeStatus = 'pending' | 'done' | 'partial' | 'failed' | 'skipped';

export interface Barcode {
  /** Index of the page it is on. */
  page: number;
  format: BarcodeFormat;
  text: string;
  /** Detection pass that found it: the page turned clockwise by 0, 90, 180 or 270 degrees. */
  rotation: 0 | 90 | 180 | 270;
  /** Reading direction on the page, degrees clockwise (0 upright, 180 upside down); absent when unknown. */
  orientation?: number;
  /** Page pixel coordinates: the scan line's ends for 1D codes, corners/finder patterns for 2D codes. */
  points: Array<{ x: number; y: number }>;
}

export interface AppStatus {
  app: 'papertrail';
  version: string;
  apiVersion: number;
  backend: 'twain' | 'mock' | string;
  paired: boolean;
  pairingAllowed: boolean;
}

export interface ScannerInfo {
  id: string;
  name: string;
  manufacturer?: string;
  backend: string;
  isDefault: boolean;
}

export interface PageInfo {
  index: number;
  mediaType: string;
  width: number;
  height: number;
  size: number;
  url: string;
  /** Only when the scan asked for barcodes. */
  barcodeStatus?: BarcodeStatus;
  barcodes?: Barcode[];
}

export interface ScanJob {
  id: string;
  status: ScanStatus;
  trigger: 'api' | 'protocolUrl';
  format: OutputFormat;
  createdAt: string;
  finishedAt?: string;
  error?: { error: string; message: string };
  pages: PageInfo[];
  /** Every page's barcodes in page order; only when the scan asked for barcodes. */
  barcodes?: Barcode[];
}

export interface ScanResult {
  id: string;
  pages: Array<PageInfo & { blob: Blob }>;
  /** Every page's barcodes in page order; only when the scan asked for barcodes. */
  barcodes?: Barcode[];
}

export interface PapertrailClientOptions {
  /** Only for testing against a dev host; papertrail itself always uses 47115. */
  port?: number;
  pollIntervalMs?: number;
  launchTimeoutMs?: number;
  scanTimeoutMs?: number;
  launch?: (url: string) => void;
  fetch?: typeof fetch;
  origin?: string;
  storage?: Storage | null;
}

export declare class PapertrailError extends Error {
  readonly code: string;
  readonly details?: unknown;
  constructor(code: string, message: string, details?: unknown);
}

export declare class PapertrailClient {
  readonly port: number;
  readonly baseUrl: string;
  readonly origin: string | undefined;
  constructor(options?: PapertrailClientOptions);
  loopbackPermission(): Promise<'granted' | 'prompt' | 'denied' | 'unsupported'>;
  status(): Promise<AppStatus | null>;
  pair(): Promise<{ paired: boolean }>;
  connect(options?: { launch?: boolean }): Promise<AppStatus>;
  listScanners(): Promise<ScannerInfo[]>;
  scan(options?: ScanOptions, hooks?: { onProgress?: (job: ScanJob) => void; signal?: AbortSignal }): Promise<ScanResult>;
}

export declare function buildScanUrl(origin: string, requestId: string, options?: ScanOptions): string;
