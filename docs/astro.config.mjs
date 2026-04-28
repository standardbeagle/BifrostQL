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
					],
				},
				{
					label: 'Core Concepts',
					items: [
						{ label: 'Schema Generation', slug: 'concepts/schema-generation' },
						{ label: 'Solving N+1 Queries', slug: 'concepts/n-plus-one' },
						{ label: 'App Schema Detection', slug: 'concepts/app-schema-detection' },
					],
				},
				{
					label: 'Guides',
					items: [
						{ label: 'Queries', slug: 'guides/queries' },
						{ label: 'Joins', slug: 'guides/joins' },
						{ label: 'Mutations', slug: 'guides/mutations' },
						{ label: 'Module System', slug: 'guides/modules' },
						{ label: 'Authentication', slug: 'guides/authentication' },
					{ label: 'Desktop App', slug: 'guides/desktop-app' },
						{ label: 'WordPress', slug: 'guides/wordpress' },
						{ label: 'Binary Transport', slug: 'guides/binary-transport' },
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
