import { describe, it, expect } from 'vitest';
import {
  MEMBERS_FINANCE,
  FINANCE_FIELDS_BY_ENTITY,
  canReadFinanceFields,
  isFinanceField,
  gateFinanceFields,
} from './finance-fields';

/**
 * The finance-field read-gate mirrors the host's `policy-read-deny` on
 * `price_cents` / `amount_cents`, qualified to
 * `policy-read-deny-roles: officer,event_manager,member,read_only`. These
 * tests pin the SPA-side gate to that server policy: finance_manager + admin
 * sessions (which hold `main.members.finance`) see the columns; the four
 * denied roles do not.
 */
describe('finance-fields gate', () => {
  // The roles the server denies finance reads to — none of them hold the
  // finance permission in the SPA session.
  const deniedRolePermissions: Record<string, string[]> = {
    officer: ['main.members.read', 'main.members.write'],
    event_manager: ['main.members.read', 'main.members.write'],
    member: ['main.members.read'],
    read_only: ['main.members.read'],
  };

  // The roles the server allows finance reads to — they hold the finance
  // permission in the SPA session.
  const allowedRolePermissions: Record<string, string[]> = {
    finance_manager: [
      'main.members.read',
      'main.members.write',
      MEMBERS_FINANCE,
    ],
    admin: [
      'main.members.read',
      'main.members.write',
      'main.members.admin',
      MEMBERS_FINANCE,
    ],
  };

  describe('canReadFinanceFields', () => {
    it('is false for every server-denied role', () => {
      for (const [role, permissions] of Object.entries(
        deniedRolePermissions,
      )) {
        expect(canReadFinanceFields(permissions), role).toBe(false);
      }
    });

    it('is true for finance_manager and admin', () => {
      for (const [role, permissions] of Object.entries(
        allowedRolePermissions,
      )) {
        expect(canReadFinanceFields(permissions), role).toBe(true);
      }
    });
  });

  describe('isFinanceField', () => {
    it('flags price_cents on membership_plans and amount_cents on dues entities', () => {
      expect(
        isFinanceField('price_cents', 'main.membership_plans'),
      ).toBe(true);
      expect(
        isFinanceField('amount_cents', 'main.dues_invoices'),
      ).toBe(true);
      expect(
        isFinanceField('amount_cents', 'main.dues_payments'),
      ).toBe(true);
    });

    it('does not flag non-finance columns', () => {
      expect(isFinanceField('name', 'main.membership_plans')).toBe(false);
      expect(isFinanceField('paid_on', 'main.dues_payments')).toBe(false);
      expect(isFinanceField('status', 'main.dues_invoices')).toBe(false);
    });

    it('scopes to the entity when one is given', () => {
      // price_cents is not a finance column on dues_invoices.
      expect(isFinanceField('price_cents', 'main.dues_invoices')).toBe(false);
      // ...but is recognised entity-agnostically.
      expect(isFinanceField('price_cents')).toBe(true);
    });
  });

  describe('gateFinanceFields', () => {
    const planColumns = [
      'name',
      'billing_period',
      'price_cents',
      'is_active',
    ];

    it('drops finance columns for a denied role', () => {
      const gated = gateFinanceFields(
        planColumns,
        'main.membership_plans',
        deniedRolePermissions.officer,
      );
      expect(gated).toEqual(['name', 'billing_period', 'is_active']);
      expect(gated).not.toContain('price_cents');
    });

    it('leaves the list unchanged for a finance role', () => {
      const gated = gateFinanceFields(
        planColumns,
        'main.membership_plans',
        allowedRolePermissions.finance_manager,
      );
      expect(gated).toEqual(planColumns);
    });

    it('drops amount_cents from dues entities for a denied role', () => {
      const invoiceColumns = [
        'member_id',
        'amount_cents',
        'issued_on',
        'due_on',
        'status',
      ];
      expect(
        gateFinanceFields(
          invoiceColumns,
          'main.dues_invoices',
          deniedRolePermissions.member,
        ),
      ).toEqual(['member_id', 'issued_on', 'due_on', 'status']);
    });
  });

  it('FINANCE_FIELDS_BY_ENTITY covers exactly the three policy-deny entities', () => {
    expect(Object.keys(FINANCE_FIELDS_BY_ENTITY).sort()).toEqual([
      'main.dues_invoices',
      'main.dues_payments',
      'main.membership_plans',
    ]);
  });
});
