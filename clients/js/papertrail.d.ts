export declare const DEFAULT_PORT: 47115;

export type ColorMode = 'color' | 'gray' | 'blackWhite';
export type PaperSource = 'auto' | 'flatbed' | 'feeder';
export type ScanStatus = 'queued' | 'scanning' | 'completed' | 'failed' | 'canceled';
/** Only 'png' (one image per page) today. */
export type OutputFormat = 'png';

export type StampPosition = 'top' | 'bottom';

/**
 * Asks papertrail to add its provenance band to each page. The page can only choose the
 * position: every value in the band comes from papertrail. Policy may also require the stamp.
 */
export interface StampOptions {
  position?: StampPosition;
}

/** Printed in the band, embedded in the PNG (iTXt "papertrail:provenance") and returned here. */
export interface PageProvenance {
  version: 1;
  /** Made by papertrail for the whole scan, e.g. "PT-7K3M-9QX2". */
  scanId: string;
  /** Local time with UTC offset, ISO 8601. */
  scannedAt: string;
  scannedAtUtc: string;
  workstation: string;
  /** DOMAIN\user */
  user: string;
  scanner: string;
  origin: string;
  page: number;
  pageCount: number;
  /** SHA-256 of the page before the band was added (canonical pixels, see hashMethod). */
  originalSha256: string;
  hashMethod: 'papertrail-pixels-v1';
  position: StampPosition;
  bandHeight: number;
  software: string;
}

export interface StampInfo {
  position: StampPosition;
  requiredByPolicy: boolean;
  scanId: string;
}

export interface ScanOptions {
  scannerId?: string;
  dpi?: number;
  colorMode?: ColorMode;
  source?: PaperSource;
  duplex?: boolean;
  showDriverUi?: boolean;
  maxPages?: number;
  format?: OutputFormat;
  stamp?: StampOptions | null;
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
  /**
   * Pixels of the page as the scanner delivered it: the scan line's ends for 1D codes, corners or
   * finder patterns for 2D codes. A provenance band on top is not included; use
   * {@link barcodePointsOnImage} to draw them on the stamped image.
   */
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
  /** Only on stamped pages. */
  provenance?: PageProvenance;
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
  /** Pages received so far. Stamped scans list their pages only once all are stamped. */
  pagesScanned: number;
  stamp?: StampInfo;
  /** Every page's barcodes in page order; only when the scan asked for barcodes. */
  barcodes?: Barcode[];
}

export interface ScanResult {
  id: string;
  pages: Array<PageInfo & { blob: Blob }>;
  stamp?: StampInfo;
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

/**
 * Maps barcode points onto the image the page downloaded. Detection runs before the stamp, so a
 * band at the top shifts every point down by `provenance.bandHeight`. A bottom band does not.
 */
export declare function barcodePointsOnImage(
  provenance: PageProvenance | null | undefined,
  points: Array<{ x: number; y: number }>,
): Array<{ x: number; y: number }>;
