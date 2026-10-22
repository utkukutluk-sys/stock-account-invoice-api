// Do not advertise the framework in production responses.
export default defineNitroPlugin((nitroApp) => {
  nitroApp.hooks.hook('render:response', (response) => {
    if (response.headers) delete response.headers['x-powered-by']
  })
  nitroApp.hooks.hook('beforeResponse', (event) => {
    event.node.res.removeHeader('x-powered-by')
  })
})
