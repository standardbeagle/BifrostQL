// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
	site: 'https://standardbeagle.github.io',
	base: '/BifrostQL',
	integrations: [
		starlight({
			title: 'BifrostQL',
			tagline: 'Zero-code GraphQL API for your existing database',
			social: [
				{ icon: 'github', label: 'GitHub', href: 'https://github.com/standardbeagle/BifrostQL' },
			],
			components: {
				Header: './src/components/Header.astro',
			},
			customCss: [
				'./src/styles/custom.css',
			],
			sidebar: [
				{
					label: 'Getting Started',
					items: [
						{ label: 'Installation & Setup', slug: 'getting-started' },
						{ label: 'Example Projects', slug: 'getting-started/examples' },
					],
				},
				{
					label: 'Core Concepts',
					items: [
						{ label: 'Schema Generation', slug: 'concepts/schema-generation' },
						{ label: 'Solving N+1 Queries', slug: 'concepts/n-plus-one' },
						{ label: 'Computed Columns & Validation', slug: 'concepts/computed-columns-and-validation' },
						{ label: 'Lookup-Table Enums', slug: 'concepts/lookup-table-enums' },
						{ label: 'Pivot / Cross-Tab', slug: 'concepts/pivot' },
						{ label: 'EAV & the _meta Field', slug: 'concepts/eav-meta' },
						{ label: 'App Schema Detection', slug: 'concepts/app-schema-detection' },
						{ label: 'App Metadata Overlay', slug: 'concepts/app-metadata-overlay' },
						{ label: 'Protocol Adapters', slug: 'concepts/protocol-adapters' },
						{ label: 'Change Data Capture & Events', slug: 'concepts/cdc-outbound-events' },
						{ label: 'Field Encryption & Masking', slug: 'concepts/field-encryption' },
						{ label: 'Temporal Change History', slug: 'concepts/temporal-history' },
					],
				},
				{
					label: 'Guides',
					items: [
						{ label: 'Queries', slug: 'guides/queries' },
						{ label: 'Joins', slug: 'guides/joins' },
						{ label: 'Mutations', slug: 'guides/mutations' },
						{ label: 'Module System', slug: 'guides/modules' },
						{ label: 'Extending BifrostQL (Hooks & Providers)', slug: 'guides/extensibility' },
						{ label: 'Authentication', slug: 'guides/authentication' },
						{ label: 'Multi-Tenant Org Model', slug: 'guides/org-model' },
						{ label: 'State Machines', slug: 'guides/state-machines' },
						{ label: 'Workflows', slug: 'guides/workflows' },
						{ label: 'Workflow Mutations & Audit Trail', slug: 'guides/workflow-mutations' },
						{ label: 'Emitting Change Events (CDC)', slug: 'guides/cdc-events' },
						{ label: 'React Hooks & Components', slug: 'guides/react-hooks' },
						{ label: 'Embeddable Data Editor', slug: 'guides/embedded-editor' },
						{ label: 'Binary Transport', slug: 'guides/binary-transport' },
						{ label: 'Authoring a Protocol Adapter', slug: 'guides/protocol-adapters' },
						{ label: 'React Native', slug: 'guides/react-native' },
						{ label: 'WordPress', slug: 'guides/wordpress' },
					],
				},
				{
					label: 'Case Studies',
					items: [
						{ label: 'Overview', slug: 'case-studies' },
						{ label: 'Web Admin for a WPF LOB App', slug: 'case-studies/wpf-lob-admin' },
						{ label: 'Two-Tier Admin: API vs. Raw SQL', slug: 'case-studies/two-tier-admin' },
						{ label: 'Multi-Tenant SaaS Back Office', slug: 'case-studies/multi-tenant-saas' },
					],
				},
				{
					label: 'Desktop Navigator',
					items: [
						{ label: 'Desktop App', slug: 'guides/desktop-app' },
						{ label: 'Visual Query Builder', slug: 'concepts/visual-query-builder' },
						{ label: 'Hosted SPA / API Mode', slug: 'guides/hosted-spa' },
					],
				},
				{
					label: 'Reference',
					items: [
						{ label: 'Configuration', slug: 'reference/configuration' },
						{ label: 'SQL Dialects', slug: 'reference/dialects' },
					],
				},
			],
		}),
	],
});
