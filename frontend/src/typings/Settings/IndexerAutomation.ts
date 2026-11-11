export default interface IndexerAutomation {
  enabled: boolean;
  protocols: string[];
  languages: string[];
  privacy: string[];
  categories: number[];
  testTimeoutSeconds: number;
  retryDays: number;
  removalDays: number;
  maxAddsPerPass: number;
}
