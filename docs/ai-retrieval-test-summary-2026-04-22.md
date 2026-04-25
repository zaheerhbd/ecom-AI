# AI Retrieval Test Summary - 2026-04-22

Test run against local API endpoint:

`http://localhost:5010/api/ai/chat`

Detailed results:

`docs/ai-retrieval-test-results-2026-04-22.csv`

## Current Threshold

`AiChatService` is currently using:

`MinimumConfidenceScore = 0.59`

## High-Level Result

21 test cases were executed.

Approximate outcome:

- 16 behaved acceptably
- 5 need improvement

The confidence gate is helping. Unsupported questions like cryptocurrency, student discounts, New York stores, warranty, returns after 30 days, and international shipping were safely rejected.

## Cases That Worked Well

Product retrieval worked well for simple product questions:

- `Show me hats`
- `Show me React products`
- `Do you have blue hats?`
- `Which hat is cheapest?`
- `Show me products from React`

Delivery retrieval worked well for simple delivery questions:

- `What delivery options are available?`
- `Do you have free delivery?`
- `Which delivery option is fastest?`
- `How long does delivery take?`

Unsupported-question rejection worked well for:

- `Do you ship internationally?`
- `Can I return a product after 30 days?`
- `What is your refund policy for damaged imports?`
- `Do you have stores in New York?`
- `Can I pay with cryptocurrency?`
- `Do you offer student discounts?`
- `What warranty comes with laptops?`

## Cases That Need Improvement

### TC-006: Products Under 20 Dollars

Question:

`What products are available under 20 dollars?`

Observed result:

The system answered with delivery options, especially `FREE delivery`, instead of products.

Why this happened:

The semantic search matched the idea of `under 20 dollars` with delivery costs. It did not understand that the user asked for products.

Improvement needed:

Add query intent handling. Product questions should filter to `sourceType = product`.

### TC-010: Cheapest Delivery Option

Question:

`Which delivery option is cheapest?`

Observed result:

The system answered `UPS3 delivery` at `$2.55`, but `FREE delivery` exists at `$0.00`.

Why this happened:

The vector search ranked text relevance, not numeric price.

Improvement needed:

Later sorting/filtering should use structured fields such as `price`, not semantic score alone.

### TC-013: Next Day Delivery

Question:

`Can I get next day delivery?`

Observed result:

The system answered with `UPS2 delivery`, which says `2-5 Days`.

Why this happened:

The result was delivery-related, but it did not actually answer the exact question.

Improvement needed:

Add an answer validation step for yes/no policy questions. If the retrieved text does not explicitly support the answer, return a safe fallback.

### TC-014: Cash On Delivery

Question:

`Do you offer cash on delivery?`

Observed result:

The system answered with delivery options even though no cash-on-delivery information was present.

Why this happened:

The phrase `delivery` caused relevant delivery documents to be retrieved, but the specific concept `cash on delivery` was unsupported.

Improvement needed:

Add unsupported-topic detection or source coverage checking. The answer should be rejected unless the retrieved content contains the key concept.

### TC-016: Discounts On Hats

Question:

`Do you have discounts on hats?`

Observed result:

The system returned hat products, but none of the sources mention discounts.

Why this happened:

The retriever matched `hats`, but ignored that the real question was about discounts.

Improvement needed:

Add source coverage checking for important terms like `discount`, `coupon`, `sale`, or `promotion`.

## Main Improvements Needed Next

1. Add basic query intent detection.

Examples:

- Product intent: `products`, `hats`, `boards`, `gloves`, `under 20`, `cheapest hat`
- Delivery intent: `delivery`, `shipping`
- Unsupported policy intent: `cash on delivery`, `discounts`, `warranty`, `crypto`, `stores`

2. Apply source-type filtering.

Examples:

- Product questions should prefer or require `sourceType = product`
- Delivery questions should prefer or require `sourceType = policy`

3. Add source coverage validation.

The top result should not only be related. It should contain enough information to answer the actual question.

Example:

`Do you offer cash on delivery?`

Matching a delivery document is not enough. The source should mention cash, COD, payment, or a similar term.

4. Add structured filtering/sorting later.

This is needed for questions like:

- `under 20 dollars`
- `cheapest`
- `most expensive`
- `fastest`

5. Improve answer wording.

The current answer style says:

`The top match is...`

Better answer style should directly answer the question:

- `Yes, free delivery is available.`
- `The cheapest delivery option is FREE delivery at $0.00.`
- `I could not find information confirming cash on delivery.`

## Recommendation

Do not tune the score threshold much more right now.

The current threshold catches many unsupported questions, but some bad answers still pass because their scores are above `0.59`.

That means the next improvement should not be only score tuning.

The next improvement should be:

`intent detection + source-type filtering + source coverage validation`

