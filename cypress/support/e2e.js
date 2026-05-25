// Cypress entrypoint. Runs once before each spec.
// Cursory's signed-in cookie is __Host-prefixed and Secure in prod / SameAsRequest in dev,
// so http://localhost:5238 + cookies work without extra ceremony.

Cypress.on('uncaught:exception', (err) => {
  // Blazor circuit warnings about disposed JS object references during teardown are noise
  // — ignore them so a clean nav doesn't fail the test.
  if (err.message && err.message.includes('disposed JS object reference')) return false;
});
