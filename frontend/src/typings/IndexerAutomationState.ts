export default interface IndexerAutomationState {
  key: string;
  name: string;
  indexerId?: number;
  failureSince?: string;
  nextAttemptAt?: string;
  lastAttemptResult?: string;
  removedAt?: string;
  suppressed: boolean;
}
