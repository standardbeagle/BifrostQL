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
    "storybook-addon-sass-postcss"
  ],
  "framework": "@storybook/react",
  "core": {
    
  },
  "features": {
    "storyStoreV7": true
  }
}