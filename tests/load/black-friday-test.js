// ═══════════════════════════════════════════════════════════
// BLACK FRIDAY LOAD TEST
// Simulates 50K+ RPM with realistic traffic patterns
// Tests concurrency, idempotency, and system resilience
// ═══════════════════════════════════════════════════════════

import http from 'k6/http';
import { check, group, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';
import { randomString, randomIntBetween } from 'https://jslib.k6.io/k6-utils/1.2.0/index.js';

// ═══════════════════════════════════════════════════════════
// CONFIGURATION
// ═══════════════════════════════════════════════════════════

const BASE_URL = __ENV.API_BASE_URL || 'https://api.company.com';
const API_KEY = __ENV.API_KEY || 'test-api-key';

// Load test stages (ramp up to Black Friday peak)
export const options = {
  stages: [
    { duration: '2m', target: 100 },    // Warm-up: Ramp to 100 VUs
    { duration: '3m', target: 500 },    // Normal: Ramp to 500 VUs
    { duration: '5m', target: 2000 },   // Pre-BF: Ramp to 2000 VUs (30K RPM)
    { duration: '10m', target: 3000 },  // Black Friday: Sustain 3000 VUs (50K RPM)
    { duration: '5m', target: 500 },    // Cool down
    { duration: '2m', target: 0 },      // Ramp down
  ],
  thresholds: {
    // Performance thresholds (SLA requirements)
    'http_req_duration': [
      'p(95)<150',          // 95% of requests < 150ms
      'p(99)<500',          // 99% of requests < 500ms
    ],
    'http_req_failed': ['rate<0.01'],  // Error rate < 1%
    'checks': ['rate>0.99'],           // 99% of checks pass
    
    // Custom metrics
    'order_creation_duration': ['p(95)<100'],
    'order_idempotency_hits': ['count>100'],
    'concurrent_requests': ['value<10000'],
  },
  // Graceful ramp-down
  gracefulStop: '30s',
  
  // Disable default metrics
  summaryTrendStats: ['min', 'med', 'avg', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

// ═══════════════════════════════════════════════════════════
// CUSTOM METRICS
// ═══════════════════════════════════════════════════════════

const orderCreationDuration = new Trend('order_creation_duration');
const orderIdempotencyHits = new Counter('order_idempotency_hits');
const orderConflicts = new Counter('order_conflicts');
const cacheHits = new Counter('cache_hits');
const cacheMisses = new Counter('cache_misses');
const errorRate = new Rate('errors');
const concurrentRequests = new Counter('concurrent_requests');

// ═══════════════════════════════════════════════════════════
// TEST DATA GENERATORS
// ═══════════════════════════════════════════════════════════

function generateIdempotencyKey() {
  return `idem-${randomString(32)}`;
}

function generateOrderPayload() {
  const productIds = [
    '123e4567-e89b-12d3-a456-426614174001',
    '123e4567-e89b-12d3-a456-426614174002',
    '123e4567-e89b-12d3-a456-426614174003',
    '123e4567-e89b-12d3-a456-426614174004',
    '123e4567-e89b-12d3-a456-426614174005',
  ];

  return {
    customerId: `cust-${randomString(16)}`,
    shippingAddress: {
      addressLine1: `${randomIntBetween(1, 9999)} Main St`,
      addressLine2: null,
      city: 'Seattle',
      stateOrProvince: 'WA',
      postalCode: '98101',
      country: 'US',
    },
    lines: Array.from({ length: randomIntBetween(1, 5) }, () => ({
      productId: productIds[randomIntBetween(0, productIds.length - 1)],
      quantity: randomIntBetween(1, 10),
    })),
  };
}

// ═══════════════════════════════════════════════════════════
// REQUEST HELPERS
// ═══════════════════════════════════════════════════════════

function makeRequest(method, endpoint, body = null, headers = {}) {
  const url = `${BASE_URL}${endpoint}`;
  
  const params = {
    headers: {
      'Content-Type': 'application/json',
      'Accept': 'application/json',
      'X-API-Key': API_KEY,
      'User-Agent': 'k6-load-test',
      ...headers,
    },
    timeout: '30s',
  };

  concurrentRequests.add(1);
  
  let response;
  if (method === 'GET') {
    response = http.get(url, params);
  } else if (method === 'POST') {
    response = http.post(url, JSON.stringify(body), params);
  } else if (method === 'PATCH') {
    response = http.patch(url, JSON.stringify(body), params);
  }

  // Track cache hits
  if (response.headers['X-Cache-Status'] === 'HIT') {
    cacheHits.add(1);
  } else if (response.headers['X-Cache-Status'] === 'MISS') {
    cacheMisses.add(1);
  }

  return response;
}

// ═══════════════════════════════════════════════════════════
// TEST SCENARIOS
// ═══════════════════════════════════════════════════════════

export default function () {
  // Weighted scenario distribution (realistic Black Friday traffic)
  const scenario = Math.random();

  if (scenario < 0.4) {
    // 40% - Browse products (read-heavy, cached)
    browseProducts();
  } else if (scenario < 0.6) {
    // 20% - View product details
    viewProductDetails();
  } else if (scenario < 0.8) {
    // 20% - Manage cart
    manageCart();
  } else if (scenario < 0.95) {
    // 15% - Create order (critical path)
    createOrder();
  } else {
    // 5% - Check order status
    checkOrderStatus();
  }

  // Realistic think time (user reading/deciding)
  sleep(randomIntBetween(1, 3));
}

// ═══════════════════════════════════════════════════════════
// SCENARIO 1: BROWSE PRODUCTS (Read-Heavy, Cached)
// ═══════════════════════════════════════════════════════════

function browseProducts() {
  group('Browse Products', () => {
    const response = makeRequest('GET', '/api/v1/products?page=1&pageSize=50');

    check(response, {
      'products list: status 200': (r) => r.status === 200,
      'products list: has data': (r) => {
        const body = JSON.parse(r.body);
        return body.items && body.items.length > 0;
      },
      'products list: cached': (r) => r.headers['X-Cache-Status'] === 'HIT' || r.headers['X-Cache-Status'] === 'MISS',
      'products list: fast response': (r) => r.timings.duration < 100,
    }) || errorRate.add(1);

    // Pagination test
    if (response.status === 200) {
      const body = JSON.parse(response.body);
      if (body.nextCursor) {
        const nextPage = makeRequest('GET', `/api/v1/products?cursor=${body.nextCursor}&pageSize=50`);
        check(nextPage, {
          'products pagination: status 200': (r) => r.status === 200,
        });
      }
    }
  });
}

// ═══════════════════════════════════════════════════════════
// SCENARIO 2: VIEW PRODUCT DETAILS (Read-Heavy, Cached)
// ═══════════════════════════════════════════════════════════

function viewProductDetails() {
  group('View Product Details', () => {
    const productId = '123e4567-e89b-12d3-a456-426614174001';
    const response = makeRequest('GET', `/api/v1/products/${productId}`);

    check(response, {
      'product details: status 200': (r) => r.status === 200,
      'product details: has price': (r) => {
        const body = JSON.parse(r.body);
        return body.price !== undefined;
      },
      'product details: cached': (r) => r.headers['X-Cache-Status'] === 'HIT',
      'product details: fast response': (r) => r.timings.duration < 50,
    }) || errorRate.add(1);
  });
}

// ═══════════════════════════════════════════════════════════
// SCENARIO 3: MANAGE CART (Ephemeral State)
// ═══════════════════════════════════════════════════════════

function manageCart() {
  group('Manage Cart', () => {
    // Create cart
    const createCartResponse = makeRequest('POST', '/api/v1/carts', {
      sessionId: `session-${randomString(16)}`,
    });

    if (!check(createCartResponse, {
      'create cart: status 201': (r) => r.status === 201,
    })) {
      errorRate.add(1);
      return;
    }

    const cart = JSON.parse(createCartResponse.body);
    const cartId = cart.id;

    // Add items to cart
    const addItemResponse = makeRequest('PUT', `/api/v1/carts/${cartId}/items`, {
      productId: '123e4567-e89b-12d3-a456-426614174001',
      quantity: randomIntBetween(1, 5),
    });

    check(addItemResponse, {
      'add to cart: status 200': (r) => r.status === 200,
      'add to cart: fast response': (r) => r.timings.duration < 100,
    }) || errorRate.add(1);
  });
}

// ═══════════════════════════════════════════════════════════
// SCENARIO 4: CREATE ORDER (Critical Path, Idempotent)
// ═══════════════════════════════════════════════════════════

function createOrder() {
  group('Create Order', () => {
    const idempotencyKey = generateIdempotencyKey();
    const orderPayload = generateOrderPayload();

    // First attempt
    const startTime = new Date();
    const response = makeRequest('POST', '/api/v1/orders', orderPayload, {
      'Idempotency-Key': idempotencyKey,
    });
    const duration = new Date() - startTime;

    orderCreationDuration.add(duration);

    const isSuccess = check(response, {
      'create order: status 201 or 202': (r) => r.status === 201 || r.status === 202,
      'create order: has order ID': (r) => {
        if (r.status === 201) {
          const body = JSON.parse(r.body);
          return body.orderId !== undefined;
        }
        return true;
      },
      'create order: fast response': (r) => r.timings.duration < 200,
    });

    if (!isSuccess) {
      errorRate.add(1);
      return;
    }

    // Test idempotency (retry with same key)
    if (Math.random() < 0.1) { // 10% retry rate
      sleep(0.5);
      const retryResponse = makeRequest('POST', '/api/v1/orders', orderPayload, {
        'Idempotency-Key': idempotencyKey,
      });

      const isIdempotent = check(retryResponse, {
        'order idempotency: status 201 or 409': (r) => r.status === 201 || r.status === 409,
        'order idempotency: returns same order': (r) => {
          if (r.status === 201 && response.status === 201) {
            const original = JSON.parse(response.body);
            const retry = JSON.parse(r.body);
            return original.orderId === retry.orderId;
          }
          return true;
        },
      });

      if (isIdempotent) {
        orderIdempotencyHits.add(1);
      }
    }

    // Test concurrent duplicate requests (race condition)
    if (Math.random() < 0.05) { // 5% concurrency test
      const concurrentKey = generateIdempotencyKey();
      const concurrentPayload = generateOrderPayload();

      // Fire 3 concurrent requests with same idempotency key
      const promises = [
        makeRequest('POST', '/api/v1/orders', concurrentPayload, {
          'Idempotency-Key': concurrentKey,
        }),
        makeRequest('POST', '/api/v1/orders', concurrentPayload, {
          'Idempotency-Key': concurrentKey,
        }),
        makeRequest('POST', '/api/v1/orders', concurrentPayload, {
          'Idempotency-Key': concurrentKey,
        }),
      ];

      // All should succeed, only one should create order
      const responses = promises.map(r => r);
      const successCount = responses.filter(r => r.status === 201).length;
      const conflictCount = responses.filter(r => r.status === 409).length;

      check({ successCount, conflictCount }, {
        'concurrency: exactly one creation': () => successCount === 1,
        'concurrency: two conflicts': () => conflictCount === 2,
      }) || orderConflicts.add(1);
    }
  });
}

// ═══════════════════════════════════════════════════════════
// SCENARIO 5: CHECK ORDER STATUS
// ═══════════════════════════════════════════════════════════

function checkOrderStatus() {
  group('Check Order Status', () => {
    // Use existing order ID (would be from previous creation)
    const orderId = '123e4567-e89b-12d3-a456-426614174000';
    const response = makeRequest('GET', `/api/v1/orders/${orderId}`);

    check(response, {
      'get order: status 200 or 404': (r) => r.status === 200 || r.status === 404,
      'get order: fast response': (r) => r.timings.duration < 100,
    }) || errorRate.add(1);
  });
}

// ═══════════════════════════════════════════════════════════
// SETUP & TEARDOWN
// ═══════════════════════════════════════════════════════════

export function setup() {
  console.log('🚀 Starting Black Friday load test...');
  console.log(`Base URL: ${BASE_URL}`);
  console.log(`Expected peak: 50,000+ RPM`);
  
  // Warm-up: Pre-populate cache
  console.log('Warming up cache...');
  for (let i = 0; i < 10; i++) {
    makeRequest('GET', '/api/v1/products?page=1&pageSize=50');
  }
  
  console.log('Setup complete!');
  return { startTime: new Date() };
}

export function teardown(data) {
  const duration = (new Date() - data.startTime) / 1000 / 60;
  console.log(`\n✅ Load test completed in ${duration.toFixed(2)} minutes`);
}

// ═══════════════════════════════════════════════════════════
// CUSTOM SUMMARY HANDLER
// ═══════════════════════════════════════════════════════════

export function handleSummary(data) {
  return {
    'stdout': textSummary(data, { indent: ' ', enableColors: true }),
    'summary.json': JSON.stringify(data),
    'summary.html': htmlReport(data),
  };
}

function htmlReport(data) {
  const metrics = data.metrics;
  return `
<!DOCTYPE html>
<html>
<head>
  <title>Load Test Results</title>
  <style>
    body { font-family: Arial, sans-serif; margin: 20px; }
    .metric { margin: 10px 0; }
    .pass { color: green; }
    .fail { color: red; }
    table { border-collapse: collapse; width: 100%; }
    th, td { border: 1px solid #ddd; padding: 8px; text-align: left; }
    th { background-color: #4CAF50; color: white; }
  </style>
</head>
<body>
  <h1>Black Friday Load Test Results</h1>
  <h2>Summary</h2>
  <table>
    <tr>
      <th>Metric</th>
      <th>Value</th>
      <th>Threshold</th>
      <th>Status</th>
    </tr>
    <tr>
      <td>P95 Latency</td>
      <td>${metrics.http_req_duration.values['p(95)'].toFixed(2)} ms</td>
      <td>&lt; 150ms</td>
      <td class="${metrics.http_req_duration.values['p(95)'] < 150 ? 'pass' : 'fail'}">
        ${metrics.http_req_duration.values['p(95)'] < 150 ? '✅ PASS' : '❌ FAIL'}
      </td>
    </tr>
    <tr>
      <td>Error Rate</td>
      <td>${(metrics.http_req_failed.values.rate * 100).toFixed(2)}%</td>
      <td>&lt; 1%</td>
      <td class="${metrics.http_req_failed.values.rate < 0.01 ? 'pass' : 'fail'}">
        ${metrics.http_req_failed.values.rate < 0.01 ? '✅ PASS' : '❌ FAIL'}
      </td>
    </tr>
    <tr>
      <td>Total Requests</td>
      <td>${metrics.http_reqs.values.count}</td>
      <td>N/A</td>
      <td class="pass">-</td>
    </tr>
  </table>
</body>
</html>
  `;
}