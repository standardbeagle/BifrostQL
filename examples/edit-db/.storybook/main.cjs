module.exports = {
  "stories": [
    "../src/**/*.stories.mdx",
    "../src/**/*.stories.@(js|jsx|ts|tsx)"
  ],

  "addons": [
    "@storybook/addon-links",
    "@storybook/addon-essentials",
    "@storybook/addon-interactions",
    "@storybook/addon-actions",
    "storybook-addon-sass-postcss",
    "@storybook/addon-mdx-gfm"
  ],

  "framework": {
    name: "@storybook/react-vite",
    options: {}
  },

  "features": {
    "storyStoreV7": true
  },

  docs: {
    autodocs: true
  }
}