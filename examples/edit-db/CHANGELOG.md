# Changelog

All notable changes to `@standardbeagle/edit-db` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

### Added
- Comprehensive JSDoc comments for all public components and hooks ([DART-Y619slzDqm04])
- SEO-optimized README with installation guide, usage examples, and API documentation ([DART-Y619slzDqm04])
- Type documentation in `src/types/schema.ts` with inline examples ([DART-Y619slzDqm04])

### Changed
- Improved documentation for `Editor`, `DataTable`, `DataEdit` components ([DART-Y619slzDqm04])
- Enhanced hook documentation for `useSchema`, `useDataTable`, `getFilterOperators` ([DART-Y619slzDqm04])

## [0.3.80] - 2025-03-18

### Added
- Character counter for text inputs with maxLength validation
- Range hints for numeric inputs with min/max constraints
- Pattern validation with custom error messages
- Enum field support with labeled options
- Content viewer component for long text and binary fields

### Changed
- Improved form layout with grouped field types (standard, boolean, content)
- Enhanced error handling in mutation operations

## [0.3.0] - 2025-02-15

### Added
- Multi-column side panel navigation for related records
- Foreign key cell popover for quick record preview
- Column visibility toggle in data table
- Row selection with bulk delete operations
- Auto-fit page size based on viewport

### Changed
- Refactored data table to use TanStack Table v8
- Improved column resizing with localStorage persistence

## [0.2.0] - 2025-01-20

### Added
- Initial release of @standardbeagle/edit-db
- React component library for database administration
- GraphQL schema introspection for automatic form generation
- Data table with sorting, filtering, and pagination
- Edit forms with validation support
- Foreign key navigation
- Tailwind CSS styling with shadcn/ui components

### Features
- Automatic form generation from database schema
- Built-in validation (required, pattern, range, length)
- Responsive data tables with column resizing
- Relationship navigation (single and multi joins)
- TypeScript support with full type definitions
