import type { Meta } from '@storybook/react';
import { Editor }  from './editor';
import { ApolloClient, InMemoryCache} from '@apollo/client'

const meta :Meta<typeof Editor> = {
  title: 'Example/Editor',
  component: Editor,
  parameters: {
    reactRouter: {
        routePath: '/'
    }
  },
//   argTypes: {
//     url: {
//         type: { name: 'string', reqired: false }
//     }
//   }
};

export default meta;

const Template: any = ({url, ...args} : { url: string }) => { 
    let client = undefined;
    if (url) client = new ApolloClient({
        uri: url,
        cache: new InMemoryCache(),
        });
    return <Editor client={client} {...args} />; 
};

export const NoUrl = Template.bind({});

NoUrl.args = {
};

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