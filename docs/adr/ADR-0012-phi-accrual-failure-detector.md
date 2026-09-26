# ADR-0012: Phi ($\Phi$) Accrual Failure Detection

## Status
Accepted

## Context
In a distributed database, nodes must detect when peers have crashed or become partitioned to trigger Raft leader elections and failover. Traditional binary heartbeat timeouts (e.g. "if no ping in 5 seconds, declare dead") fail in production environments because transient network congestion, GC pauses, or temporary CPU spikes trigger false failure detections, leading to continuous churn and election flapping.

## Decision
Hyperion implements the **$\Phi$-Accrual Failure Detector** (Hayashibara, Defago, Yared, & Katayama, 2004), as used in Apache Cassandra and Akka.

1. **Continuous Suspicion Metric**: Instead of returning a binary `alive` or `dead` verdict, the detector outputs a continuous suspicion value $\Phi \in [0, \infty)$.
2. **Dynamic Sampling**: Each node maintains a sliding window of the last $W = 1000$ heartbeat arrival intervals.
3. **Probabilistic Calculation**: The intervals are modeled as a normal distribution $(\mu, \sigma)$. For an elapsed time $t$ since the last received heartbeat:
   $$P_{\text{later}}(t) = \frac{1}{\sigma \sqrt{2\pi}} \int_{t}^{\infty} e^{-\frac{(x - \mu)^2}{2\sigma^2}} \, dx$$
   $$\Phi = -\log_{10}(P_{\text{later}}(t))$$
4. **Adaptive Thresholding**:
   - $\Phi = 8$: Corresponds to a probability of false detection of $10^{-8}$.
   - Raft elections and 2PC participant timeouts query the $\Phi$ detector, adapting dynamically to fluctuating network latency conditions.

## Alternatives Considered
- **Fixed Heartbeat Counter**: Rejected due to high vulnerability to false-positive failovers under transient network spikes.
- **SWIM (Gossip Protocol with Indirect Probing)**: Excellent for thousand-node clusters, but accrual failure detection over direct TCP connections is optimal for smaller, high-consistency Raft replica groups (3 to 15 nodes per group).

## Tradeoffs
- **Positives**: Virtually eliminates election flapping caused by transient network jitter; adapts automatically to cloud network variance.
- **Negatives**: Requires storing historical interval windows and computing Gaussian CDF approximations.

## Invariants
- Heartbeat arrival history window must maintain a minimum sample size before computing dynamic $\sigma$; a conservative static fallback is used during initial warmup.

## Failure Consequences
Misconfigured thresholds could cause delayed failover (if $\Phi$ threshold is too high) or cluster instability (if threshold is too low).
