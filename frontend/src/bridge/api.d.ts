import type { TerminalAPI, PlatformInfo } from './terminal-api';

declare global {
  interface Window {
    terminalAPI: TerminalAPI;
    platformInfo: PlatformInfo;
  }
}
