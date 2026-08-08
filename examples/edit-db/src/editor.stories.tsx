import type { Meta } from '@storybook/react';
import { Editor }  from './editor';

const meta :Meta<typeof Editor> = {
  title: 'Example/Editor',
  component: Editor,
  parameters: {
    reactRouter: {
        routePath: '/'
    }
  },
};

export default meta;

const Template: any = ({url, ...args} : { url: string }) => {
    return <Editor uri={url || undefined} {...args} />;
};

// There is deliberately no "no config" story: Editor throws when given neither
// `uri` nor `fetcher`, so such a story would unmount the Storybook tree rather
// than render anything useful.

export const LocalConnection = Template.bind({});
LocalConnection.args = {
    uri:  'https://localhost:7077/graphql',
}

export const uriParameter = Template.bind({});
uriParameter.args = {
    url: 'https://localhost:7077/graphql',
}

export const editParticipant = Template.bind({});
editParticipant.args = {
    url: 'https://localhost:7077/graphql',
    uiPath: '/participants/edit/5326'
}
