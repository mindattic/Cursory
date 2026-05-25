/// <reference types="cypress" />

// Smoke-test the level dropdown and reset-vote UI exist post-login. We can't easily test
// the 2/3-majority pass with a single-browser session — Cypress runs in one tab — but we
// CAN confirm that selecting a different level in a single-cursor room triggers an
// immediate switch (quorum = ceil(2/3 × 1) = 1, initiator's auto-YES passes).
describe('Cursory HUD — level select + reset', () => {
  beforeEach(() => {
    cy.visit('/login');
    cy.get('input[name="username"]').type('gungreeneyes');
    cy.get('input[name="password"]').type('Happygirl1005');
    cy.get('form.login-card').submit();
    cy.get('#room-canvas', { timeout: 15000 }).should('be.visible');
  });

  it('exposes a 14-option level dropdown', () => {
    cy.get('#room-level-select option').should('have.length', 14);
  });

  it('exposes a Reset button', () => {
    cy.get('#room-reset-btn').should('be.visible').and('contain.text', 'Reset');
  });

  it('shows the connection status pill', () => {
    cy.get('#room-status', { timeout: 10000 })
      .should('have.class', 'room-status-connected')
      .and('contain.text', 'Live');
  });

  it('switches level when the dropdown changes (solo quorum = 1)', () => {
    cy.get('#room-level-select').select('3');
    // The level banner pops on switch. Give the server a beat to fire LevelLoaded.
    cy.get('#room-level-banner', { timeout: 10000 }).should('be.visible');
    cy.get('#room-level-banner-title').should('contain.text', 'Level 3');
  });
});
