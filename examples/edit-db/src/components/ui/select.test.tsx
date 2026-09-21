import { describe, expect, it, beforeAll } from 'vitest';
import { render } from '@testing-library/react';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from './select';

// jsdom lacks the pointer/scroll APIs Radix Select touches when it opens.
beforeAll(() => {
    Element.prototype.hasPointerCapture ??= () => false;
    Element.prototype.setPointerCapture ??= () => undefined;
    Element.prototype.releasePointerCapture ??= () => undefined;
    Element.prototype.scrollIntoView ??= () => undefined;
});

describe('SelectContent', () => {
    it('opens as a popper anchored to the trigger by default', () => {
        // Radix "item-aligned" mode slides the list so the selected item covers
        // the trigger; inside a scrolling dialog that put a long FK list well
        // away from its label. Popper mode is the one that renders beneath the
        // trigger — and only popper mounts the popper content wrapper.
        render(
            <Select open value="a">
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                    <SelectItem value="a">A</SelectItem>
                    <SelectItem value="b">B</SelectItem>
                </SelectContent>
            </Select>,
        );
        expect(document.querySelector('[data-radix-popper-content-wrapper]')).not.toBeNull();
        expect(document.querySelector('[data-slot="select-content"]')).not.toBeNull();
    });

    it('still honours an explicit item-aligned position', () => {
        render(
            <Select open value="a">
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent position="item-aligned">
                    <SelectItem value="a">A</SelectItem>
                </SelectContent>
            </Select>,
        );
        expect(document.querySelector('[data-radix-popper-content-wrapper]')).toBeNull();
        expect(document.querySelector('[data-slot="select-content"]')).not.toBeNull();
    });
});
