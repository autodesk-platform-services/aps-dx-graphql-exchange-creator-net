using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using DesignAutomationFramework;

namespace ExchangeViewCreator
{
    // JSON is handled with DataContractJsonSerializer, available both on .NET Framework 4.8 (Revit 2023/2024) and on
    // .NET 8+ (Revit 2025+), so the bundle needs no extra assemblies that could conflict with Revit's own.

    /// <summary>
    /// Content of the "selection.json" input of the work item.
    /// </summary>
    [DataContract]
    public class SelectionInput
    {
        [DataMember(Name = "viewName")] public string ViewName { get; set; }
        // External IDs as seen in the Viewer, which for Revit designs are the element UniqueIds
        [DataMember(Name = "externalIds")] public List<string> ExternalIds { get; set; } = new List<string>();
        // Names of the 3D views ACC extracted from the source design, i.e., the views listed in its publish settings
        [DataMember(Name = "publishedViews")] public List<string> PublishedViews { get; set; } = new List<string>();
        // False when the result becomes a new version of the source file: an existing view with the requested name is
        // then kept, and the job fails, rather than deleting a view of the real design (missing means true)
        [DataMember(Name = "replaceExistingView")] public bool? ReplaceExistingView { get; set; }
    }

    /// <summary>
    /// Summary written to the work item report, picked up by the web application.
    /// </summary>
    [DataContract]
    public class ViewResult
    {
        [DataMember] public string ViewName { get; set; }
        [DataMember] public int Requested { get; set; }
        [DataMember] public int Isolated { get; set; }
        [DataMember] public List<string> Missing { get; set; } = new List<string>();
        // Name of the published view that was repurposed, if any
        [DataMember] public string ReusedView { get; set; }
        // Publish view/sheet sets the new view was added to, so that ACC extracts it
        [DataMember] public List<string> PublishedIn { get; set; } = new List<string>();
    }

    public class Commands : IExternalDBApplication
    {
        private const string SelectionFileName = "selection.json";
        private const string ResultFileName = "result.rvt";
        // The web application looks for this marker in the work item report
        private const string ResultMarker = "EXCHANGE_VIEW_RESULT:";

        public ExternalDBApplicationResult OnStartup(ControlledApplication application)
        {
            DesignAutomationBridge.DesignAutomationReadyEvent += HandleDesignAutomationReadyEvent;
            return ExternalDBApplicationResult.Succeeded;
        }

        public ExternalDBApplicationResult OnShutdown(ControlledApplication application)
        {
            DesignAutomationBridge.DesignAutomationReadyEvent -= HandleDesignAutomationReadyEvent;
            return ExternalDBApplicationResult.Succeeded;
        }

        private void HandleDesignAutomationReadyEvent(object sender, DesignAutomationReadyEventArgs e)
        {
            try
            {
                e.Succeeded = Run(e.DesignAutomationData);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex}");
                e.Succeeded = false;
            }
        }

        private static bool Run(DesignAutomationData data)
        {
            // 1. The input Revit file is opened by Design Automation (see the /i argument of the activity)
            var doc = data.RevitDoc;
            if (doc == null)
            {
                Console.WriteLine("ERROR: Could not open the input Revit file.");
                return false;
            }
            Console.WriteLine($"Opened document: {doc.Title} (workshared: {doc.IsWorkshared})");

            var selectionPath = Path.Combine(Directory.GetCurrentDirectory(), SelectionFileName);
            var input = ReadJson<SelectionInput>(selectionPath);
            // Lists missing from the JSON are not initialized by the serializer
            input.ExternalIds = input.ExternalIds ?? new List<string>();
            input.PublishedViews = input.PublishedViews ?? new List<string>();
            if (input == null || string.IsNullOrWhiteSpace(input.ViewName) || input.ExternalIds.Count == 0)
            {
                Console.WriteLine("ERROR: The selection input must specify a view name and at least one external ID.");
                return false;
            }

            // 2. Prepare a 3D view showing only the selected elements
            var result = CreateIsolatedView(doc, input);
            Console.WriteLine($"{ResultMarker} {ToJson(result)}");
            if (result.Isolated == 0)
            {
                Console.WriteLine("ERROR: None of the external IDs matched an element of the design.");
                return false;
            }

            // 3. Save the modified design as a new file, uploaded by Design Automation as the "result" output
            var saveOptions = new SaveAsOptions { OverwriteExistingFile = true };
            if (doc.IsWorkshared)
            {
                saveOptions.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
            }
            doc.SaveAs(Path.Combine(Directory.GetCurrentDirectory(), ResultFileName), saveOptions);
            Console.WriteLine($"Saved {ResultFileName} with view '{result.ViewName}'.");
            return true;
        }

        private static T ReadJson<T>(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
            }
        }

        private static string ToJson<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static ViewResult CreateIsolatedView(Document doc, SelectionInput input)
        {
            var result = new ViewResult { ViewName = input.ViewName, Requested = input.ExternalIds.Count };
            var elementIds = new List<ElementId>();
            foreach (var externalId in input.ExternalIds.Distinct())
            {
                var element = doc.GetElement(externalId);
                if (element == null || element is ElementType || element.Category == null)
                {
                    result.Missing.Add(externalId);
                    continue;
                }
                elementIds.Add(element.Id);
            }
            result.Isolated = elementIds.Count;
            if (elementIds.Count == 0)
            {
                return result;
            }

            View3D view;
            using (var transaction = new Transaction(doc, "Create exchange view"))
            {
                transaction.Start();

                view = FindPublishedView(doc, input);
                if (view != null)
                {
                    result.ReusedView = view.Name;
                    Console.WriteLine($"Reusing the published view '{view.Name}'.");
                }

                // The view name must be exactly the one requested, since the exchange is created from it
                var conflictingViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && v.Name == input.ViewName && v.Id != view?.Id)
                    .Select(v => v.Id)
                    .ToList();
                if (conflictingViews.Count > 0)
                {
                    if (input.ReplaceExistingView == false)
                    {
                        throw new InvalidOperationException($"The design already has a view named '{input.ViewName}'. Choose another view name.");
                    }
                    Console.WriteLine($"Deleting {conflictingViews.Count} other view(s) named '{input.ViewName}'.");
                    doc.Delete(conflictingViews);
                }

                if (view != null)
                {
                    ResetView(doc, view, elementIds);
                }
                else
                {
                    // No publish settings to satisfy: a new view will do
                    var viewFamilyType = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>()
                        .First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
                    view = View3D.CreateIsometric(doc, viewFamilyType.Id);
                }
                view.Name = input.ViewName;

                // Isolate the selected elements, and make the isolation permanent (i.e., hide everything else)
                view.IsolateElementsTemporary(elementIds);
                view.ConvertTemporaryHideIsolateToPermanent();
                HideOtherCategories(doc, view, elementIds);
                SetSectionBoxAroundSelection(doc, view, elementIds);

                transaction.Commit();
            }

            // A reused published view is extracted already; a new view must be added to the published sets
            if (result.ReusedView == null)
            {
                PublishView(doc, view, result);
            }
            return result;
        }

        // The publish settings (the views ACC extracts) are stored in the "ExportViewSheetSetListSchema" extensible storage
        // entity of the project information: a list of the IDs of the view/sheet sets to publish. The schema is readable
        // by any add-in (its writing is reserved to Autodesk), and the sets themselves can be edited through the print
        // manager's view/sheet settings; assigning ViewSheetSet.Views directly does not persist.
        private static List<ViewSheetSet> GetPublishedSets(Document doc)
        {
            var schema = Schema.ListSchemas().FirstOrDefault(s => s.SchemaName == "ExportViewSheetSetListSchema" && s.VendorId == "ADSK");
            var entity = schema == null ? null : doc.ProjectInformation.GetEntity(schema);
            if (entity == null || !entity.IsValid())
            {
                return new List<ViewSheetSet>();
            }
            var ids = new HashSet<string>(entity.Get<IList<int>>("ExportViewViewSheetSetIdList").Select(id => id.ToString()));
            return new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>()
                .Where(set => ids.Contains(set.Id.ToString()))
                .ToList();
        }

        private static void PublishView(Document doc, View view, ViewResult result)
        {
            var sets = GetPublishedSets(doc);
            if (sets.Count == 0)
            {
                Console.WriteLine("No publish settings: ACC extracts the default views.");
                return;
            }
            using (var transaction = new Transaction(doc, "Publish exchange view"))
            {
                transaction.Start();
                var printManager = doc.PrintManager;
                printManager.PrintRange = PrintRange.Select;
                var setting = printManager.ViewSheetSetting;
                foreach (var set in sets)
                {
                    setting.CurrentViewSheetSet = set;
                    var views = new ViewSet();
                    foreach (View existing in setting.CurrentViewSheetSet.Views)
                    {
                        views.Insert(existing);
                    }
                    views.Insert(view);
                    setting.CurrentViewSheetSet.Views = views;
                    setting.Save();
                    result.PublishedIn.Add(set.Name);
                }
                transaction.Commit();
            }
            Console.WriteLine($"Added view '{view.Name}' to the published view/sheet sets: {string.Join(", ", result.PublishedIn)}");
        }

        // When a Revit file has publish settings, ACC only extracts the views listed there, and Data Exchange can only
        // use extracted views. The Revit API cannot modify the publish settings, so instead of creating a new view,
        // one of the published 3D views is repurposed (this only affects the "_temp" copy of the design).
        private static View3D FindPublishedView(Document doc, SelectionInput input)
        {
            if (input.PublishedViews.Count == 0)
            {
                return null;
            }
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(v => !v.IsTemplate)
                .ToList();
            return input.PublishedViews
                .Select(name => views.FirstOrDefault(v => v.Name == name))
                .FirstOrDefault(v => v != null);
        }

        // Isolation hides other elements one by one; hiding every model category none of the selected elements belongs
        // to also covers elements isolation may not catch.
        private static void HideOtherCategories(Document doc, View view, List<ElementId> elementIds)
        {
            var keep = new HashSet<ElementId>();
            foreach (var category in elementIds.Select(id => doc.GetElement(id).Category))
            {
                keep.Add(category.Id);
                if (category.Parent != null)
                {
                    keep.Add(category.Parent.Id); // Hiding the parent would also hide the subcategory
                }
            }
            // Spatial categories are listed explicitly, in case they are not reported as model categories
            var spatial = new[] { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Areas, BuiltInCategory.OST_MEPSpaces }
                .Select(builtIn => new ElementId(builtIn))
                .ToHashSet();
            var hidden = new List<string>();
            foreach (Category category in doc.Settings.Categories)
            {
                var candidate = category.CategoryType == CategoryType.Model || spatial.Contains(category.Id);
                if (!candidate || keep.Contains(category.Id) || !view.CanCategoryBeHidden(category.Id))
                {
                    continue;
                }
                if (!view.GetCategoryHidden(category.Id))
                {
                    view.SetCategoryHidden(category.Id, true);
                    hidden.Add(category.Name);
                }
            }
            Console.WriteLine($"Hid {hidden.Count} other model categories: {string.Join(", ", hidden)}");
        }

        // Exchanges can list room elements overlapping the view's 3D extent, even when the view doesn't display rooms
        // (seen with native views boxed around stairs). A section box fitted to the selection keeps that to a minimum.
        // (Room volumes in every exchange, whatever the view, came from the design being upgraded to a newer Revit
        // version; the web app avoids that by running the engine matching the design's version.)
        private static void SetSectionBoxAroundSelection(Document doc, View3D view, List<ElementId> elementIds)
        {
            const double padding = 0.1; // ft, so that the box doesn't clip the elements themselves
            BoundingBoxXYZ box = null;
            foreach (var id in elementIds)
            {
                var bounds = doc.GetElement(id).get_BoundingBox(null);
                if (bounds == null)
                {
                    continue;
                }
                if (box == null)
                {
                    box = new BoundingBoxXYZ { Min = bounds.Min, Max = bounds.Max };
                    continue;
                }
                box.Min = new XYZ(Math.Min(box.Min.X, bounds.Min.X), Math.Min(box.Min.Y, bounds.Min.Y), Math.Min(box.Min.Z, bounds.Min.Z));
                box.Max = new XYZ(Math.Max(box.Max.X, bounds.Max.X), Math.Max(box.Max.Y, bounds.Max.Y), Math.Max(box.Max.Z, bounds.Max.Z));
            }
            if (box == null)
            {
                Console.WriteLine("No bounding box for the selection; leaving the view without a section box.");
                return;
            }
            box.Min -= new XYZ(padding, padding, padding);
            box.Max += new XYZ(padding, padding, padding);
            view.SetSectionBox(box);
            Console.WriteLine($"Section box set around the selection: {box.Min} - {box.Max}");
        }

        // Clears whatever could hide the selected elements in a repurposed view
        private static void ResetView(Document doc, View3D view, List<ElementId> elementIds)
        {
            view.ViewTemplateId = ElementId.InvalidElementId;
            if (view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate))
            {
                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            }
            view.IsSectionBoxActive = false; // Replaced by a box around the selection, see SetSectionBoxAroundSelection
            view.CropBoxActive = false;
            foreach (var filterId in view.GetFilters())
            {
                view.RemoveFilter(filterId);
            }
            var hiddenElements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(e => e.IsHidden(view))
                .Select(e => e.Id)
                .ToList();
            if (hiddenElements.Count > 0)
            {
                view.UnhideElements(hiddenElements);
            }
            foreach (var categoryId in elementIds.Select(id => doc.GetElement(id).Category.Id).Distinct())
            {
                if (view.CanCategoryBeHidden(categoryId) && view.GetCategoryHidden(categoryId))
                {
                    view.SetCategoryHidden(categoryId, false);
                }
            }
        }
    }
}
