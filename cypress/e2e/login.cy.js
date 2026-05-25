/// <reference types="cypress" />

// Smoke test for the seeded-user login path. Hits the form, posts username + password +
// antiforgery, and asserts the canvas renders on the destination.
describe('Cursory login + room', () => {
  it('signs GunGreenEyes in and lands on the room', () => {
    cy.visit('/login');
    cy.get('input[name="username"]').type('GunGreenEyes');
    cy.get('input[name="password"]').type('Happygirl1005');
    cy.get('form.login-card').submit();
    cy.url().should('eq', `${Cypress.config('baseUrl')}/`);
    cy.get('#room-canvas', { timeout: 15000 }).should('be.visible');
    cy.get('.room-hud-name').should('contain.text', 'GunGreenEyes');
  });

  it('rejects the wrong password', () => {
    cy.visit('/login');
    cy.get('input[name="username"]').type('GideonKain');
    cy.get('input[name="password"]').type('wrong-password-x');
    cy.get('form.login-card').submit();
    cy.url().should('include', '/login');
    cy.url().should('include', 'error=invalid');
    cy.get('.login-error').should('be.visible');
  });

  it('redirects unauthenticated visits at / back to /login', () => {
    cy.visit('/');
    cy.url().should('include', '/login');
  });
});
