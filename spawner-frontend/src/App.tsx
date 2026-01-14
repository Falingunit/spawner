import { Sidebar, SidebarContent, SidebarGroup, SidebarHeader } from "./components/ui/sidebar"

export function App() {
  return (
    <>
    <Sidebar>
      <SidebarHeader />
      <SidebarContent>
        <SidebarGroup> 
          Hi
        </SidebarGroup>
        <SidebarGroup />
      </SidebarContent>
    </Sidebar>
    </>
  )
}

export default App
